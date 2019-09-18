#r "Newtonsoft.Json"

#load "..\Common\requestParameters.csx"

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Azure.Management.Fluent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static async Task<IActionResult> Run(HttpRequest req, ILogger log, ExecutionContext executionContext)
{
    string containerName = GetParameter(req, log, "container") ?? "container";
    string imageId = GetParameter(req, log, "image") ?? "image";
    string buildId = GetParameter(req, log, "buildId") ?? "buildId";
    bool passedQa = bool.Parse(GetParameter(req, log, "passed") ?? "false");
    string acrRegistry = GetParameter(req, log, "acrRegistry") ?? "acrRegistry";
    string acrRepository = GetParameter(req, log, "acrRepository") ?? "acrRepository";
    string acrAuth = GetParameter(req, log, "acrAuth") ?? "acrAuth";
    string aciResourceGroup = GetParameter(req, log, "aciResourceGroup") ?? "aciResourceGroup";
    string project = GetParameter(req, log, "project") ?? "project";
    string targetBranch = GetParameter(req, log, "targetBranch") ?? "targetBranch";
    string team = GetParameter(req, log, "team") ?? "team";
    string pat = GetParameter(req, log, "pat") ?? "pat";
    string org = GetParameter(req, log, "org") ?? "org";

    var client = new HttpClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", pat);

    var url = $"https://dev.azure.com/{org}/{project}/_apis/build/builds/{buildId}?api-version=5.0";
    log.LogInformation($"url = {url}");
    var build = await client.GetAsync(url);
    var buildContent = await build.Content.ReadAsStringAsync();
    if (build.StatusCode != HttpStatusCode.OK)
    {
        return new BadRequestObjectResult(buildContent);
    }
    var buildData = (JObject)JsonConvert.DeserializeObject(buildContent);
    var repositoryId = buildData["repository"]["id"].ToString();
    log.LogInformation($"repository id = {repositoryId}");

    var sourceBranch = buildData["sourceBranch"].ToString();  // includes "refs/heads/"
    sourceBranch = sourceBranch.Substring(sourceBranch.LastIndexOf("/")+1);
    log.LogInformation($"source branch = {sourceBranch}");

    var error = false;
    var redirect = string.Empty;

    try
    {
        if (passedQa)
        {
            // Azure Container Registry (ACR)
            error = await DeleteAcrImage(log, acrAuth, acrRegistry, acrRepository, imageId);

            // Azure Container Instances (ACI)
            error = error || DeleteAciInstance(log, executionContext.FunctionAppDirectory, aciResourceGroup, imageId);

            // complete the PR in AzDO
            await AzdoCompletePullRequest(log, client, project, repositoryId, sourceBranch, targetBranch, org);
        }
        else
        {
            var projectId = buildData["definition"]["project"]["id"].ToString();
            log.LogInformation($"project id = {projectId}");
            redirect = await AzdoCreateBug(log, client, project, team, buildId, projectId, repositoryId, sourceBranch, containerName, org);
        }            
    }
    catch (Exception ex)
    {
        log.LogError($"\nError creating AzDO artifact (PR or Bug):\n{ex.Message}");
        error = true;
    }

    log.LogInformation("done");
    if (error)
    {
        return new BadRequestObjectResult("Delete function did not complete successfully; please contact Development");
    }
    else if (!string.IsNullOrEmpty(redirect))
    {
        // this happens mhen a bug is created
        return new RedirectResult(redirect);
    }
    else
    {
        return new OkObjectResult($"Deleted ACI container instance {containerName} and ACR image {imageId}");
    }
}

static bool DeleteAciInstance(ILogger log, string functionAppDirectory, string resourceGroup, string imageName)
{
    log.LogInformation("***** DeleteAciInstance *****");

    // for ACI delete using Azure Management classes & container name (no REST API available)
    var authFilePath = $@"{functionAppDirectory}\Common\my.azureauth";
    try
    {
        //to generate credentials file: "az ad sp create-for-rbac --sdk-auth > my.azureauth"
        log.LogInformation($"Authenticating with Azure using credentials in file at {authFilePath}");

        IAzure azure = Azure.Authenticate(authFilePath).WithDefaultSubscription();
        var sub = azure.GetCurrentSubscription();
        log.LogInformation($"Authenticated with subscription '{sub.DisplayName}' (ID: {sub.SubscriptionId})");

        var containerId = $"/subscriptions/{sub.SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.ContainerInstance/containerGroups/{imageName}";  
        log.LogInformation($"deleting container id = {containerId}");
        azure.ContainerGroups.DeleteById(containerId);
        log.LogInformation($"ACI container {containerId} deleted");

        return false;
    }
    catch (Exception ex)
    {
        log.LogError($"\nError deleting ACI container instance:\n{ex.Message}");

        if (string.IsNullOrEmpty(authFilePath))
        {
            log.LogWarning("Have you set the AZURE_AUTH_LOCATION environment variable?");
        }
        return true;
    }
}

static async Task<bool> DeleteAcrImage(ILogger log, string authHeader, string registry, string repository, string imageId)
{
    log.LogInformation("***** DeleteAcrImage *****");

    try
    {
        // delete using REST API & image tag
        var client = new HttpClient();

        log.LogInformation($"acr auth header = {authHeader}");  // auth header value is base 64 encoded username/password
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));

        var manifest = await client.GetAsync($"https://{registry}.azurecr.io:443/v2/{repository}/manifests/{imageId}");
        if (manifest.IsSuccessStatusCode)
        {
            var etag = manifest.Headers.ETag.Tag;
            log.LogInformation($"deleting image with manifest id = {etag}");
            await client.DeleteAsync($"https://{registry}.azurecr.io/v2/{repository}/manifests/{etag.Replace("\"", "")}");
            log.LogInformation("ACR image deleted");
        }
        else
        {
            log.LogWarning($"No manifest found for image {imageId} in repository {registry}.{repository}");
        }
        return false;
    }
    catch (Exception ex)
    {
        log.LogError($"\nError deleting ACR image:\n{ex.Message}");
        return true;
    }
}

static async Task<string> AzdoCreateBug(ILogger log, HttpClient client, string project, string team,
    string buildId, string projectId, string repositoryId, string sourceBranch, string containerName, string org)
{
    log.LogInformation($"***** creating bug for branch '{sourceBranch}' *****");

    // find current iteration
    var iterationUrl = $"https://dev.azure.com/{org}/{project}/{team}/_apis/work/teamsettings/iterations?$timeframe=current&api-version=5.0";
    log.LogInformation($"iteration url = {iterationUrl}");

    var iterationResult = await client.GetAsync(iterationUrl);
    var iterationContent = await iterationResult.Content.ReadAsStringAsync();
    string currentIteration = string.Empty;
    if (iterationResult.StatusCode == HttpStatusCode.OK)
    {
        var data = (JArray)((JObject)JsonConvert.DeserializeObject(iterationContent))["value"];
        currentIteration = (data.First()["path"]).ToString().Replace(@"\", @"\\");  // have to escape the backslash
        log.LogInformation($"current iteration for {team.Replace("%20", " ")} = {currentIteration}");
        currentIteration = $@"{{
            ""op"": ""add"",
            ""path"": ""/fields/System.IterationPath"",
            ""value"": ""{currentIteration}""
        }},";
    }
    else
    {
        log.LogInformation("no iteration found");
    }
    
    var workItemsUrl = $"https://dev.azure.com/{org}/{project}/_apis/build/builds/{buildId}/workitems?api-version=5.0";
    log.LogInformation($"work items url = {workItemsUrl}");
    var workitems = await client.GetAsync(workItemsUrl);
    var workitemsData = (JArray)((JObject)JsonConvert.DeserializeObject(await workitems.Content.ReadAsStringAsync()))["value"];
    var workitemLinks = workitemsData.Select(x => x["id"]);
    log.LogInformation($"{workitemLinks.Count()} work item links found");

    var wi = new StringBuilder();
    foreach (var id in workitemLinks)
    {
        var workItemUrl = $"https://dev.azure.com/{org}/{project}/_apis/wit/workitems?ids={id}&api-version=5.0";
        log.LogInformation($"work item url = {workItemUrl}");
        var workitem = await client.GetAsync(workItemUrl);
        var workitemData = ((JArray)((JObject)JsonConvert.DeserializeObject(await workitem.Content.ReadAsStringAsync()))["value"]).First();
        if (workitemData["fields"]["System.State"].ToString() == "Closed")
        {
            log.LogInformation($"skipped {workitemData["fields"]["System.WorkItemType"]} {id}: closed");
            continue;
        }
        else if (workitemData["fields"]["System.WorkItemType"].ToString() == "Bug")
        {
            log.LogInformation($"skipped bug {id}");
            continue;
        }

        wi.AppendLine($@"
        {{
            ""op"": ""add"",
            ""path"": ""/relations/-"",
            ""value"": {{
                ""rel"": ""System.LinkTypes.Related"",
                ""url"": ""https://dev.azure.com/{org}/{project}/_workitems/edit/{id}""
            }}
        }},");
    }

    var body = $@"[
        {{
            ""op"": ""add"",
            ""path"": ""/fields/System.Title"",
            ""value"": ""<please enter title>""
        }},
        {currentIteration}
        {{
            ""op"": ""add"",
            ""path"": ""/fields/Microsoft.VSTS.Common.Priority"",
            ""value"": ""3""
        }},
        {{
            ""op"": ""add"",
            ""path"": ""/fields/Microsoft.VSTS.Build.FoundIn"",
            ""value"": ""https://dev.azure.com/{org}/{project}/_build/results?buildId={buildId}""
        }},
        {wi.ToString()}
        {{
            ""op"": ""add"",
            ""path"": ""/relations/-"",
            ""value"": {{
                ""rel"": ""ArtifactLink"",
                ""url"": ""vstfs:///Git/Ref/{projectId}%2F{repositoryId}%2FGB{sourceBranch}"",
                ""attributes"": {{
                    ""name"": ""Branch""
                }}
            }}
        }},
        {{
            ""op"": ""add"",
            ""path"": ""/fields/Microsoft.VSTS.TCM.SystemInfo"",
            ""value"": ""<div><a href=\""https://{containerName}\"" style=\""font-weight:inherit;\"">Launch Container</a><br></div>""
        }},
        {{
            ""op"": ""add"",
            ""path"": ""/fields/Microsoft.VSTS.TCM.ReproSteps"",
            ""value"": ""<div>This is a sad empty space. Please fill it with some interesting information!</div><div><span><span>&#128546;</span></span></div><div><br></div>""
        }}
    ]";
    log.LogInformation(body);

    var url = $"https://dev.azure.com/{org}/{project}/_apis/wit/workitems/$Bug?api-version=5.0";
    log.LogInformation($"post url = {url}");
    var response = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json-patch+json"));
    var contentString = await response.Content.ReadAsStringAsync();
    try
    {
        var content = (JObject)JsonConvert.DeserializeObject(contentString);
        log.LogInformation($"Bug {content["id"].ToString()} created");
        return content["_links"]["html"]["href"].ToString();
    }
    catch (Exception ex)
    {
        log.LogError($"Error creating bug; Content = {contentString}");
        throw ex;
    }
}

static async Task AzdoCompletePullRequest(ILogger log, HttpClient client, string project,
    string repositoryId, string sourceBranch, string targetBranch, string org)
{
    var pr = await GetActivePR(log, client, project, sourceBranch, org);
    if (string.IsNullOrEmpty(pr))
    {
        log.LogInformation($"cannot complete PR (no active PR found for {sourceBranch})");
    }
    else
    {
        log.LogInformation($"***** completing PR {pr} ({sourceBranch} to {targetBranch}) *****");

        // set 'succeeded' status (NOTE: this has to match the value that is defined in AzDO branch policy)
        var body = $@"{{
            ""state"": ""succeeded"",
            ""description"": ""QA testing passed"",
            ""context"": {{
                ""name"": ""passed"",
                ""genre"": ""qa""
            }}
        }}";
        
        log.LogInformation(body);
        var url = $"https://dev.azure.com/{org}/_apis/git/repositories/{repositoryId}/pullRequests/{pr}/statuses?api-version=5.0-preview.1";
        log.LogInformation($"url = {url}");
        var response = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var content = (JObject)JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync());
        log.LogInformation($"Pull request {pr}: qa/passed status set to succeeded");
    }
}

static async Task<string> GetActivePR(ILogger log, HttpClient client, string project, string sourceBranch, string org)
{
    var response = await client.GetAsync($"https://dev.azure.com/{org}/{project}/_apis/git/pullrequests?api-version=5.0");
    var content = (JArray)((JObject)JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync()))["value"];
    var data = content
        .Select(x => new { Source=x["sourceRefName"].ToString(), Status=x["status"].ToString(), Id=x["pullRequestId"].ToString() })
        .Where(x => x.Source.Contains(sourceBranch) && x.Status == "active")
        .ToList();

    if (data.Count() == 1)
    {
        return data.First().Id;
    }
    return string.Empty;
}
