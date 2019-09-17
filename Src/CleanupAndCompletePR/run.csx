#r "Newtonsoft.Json"

#load "..\Common\aci.csx"
#load "..\Common\acr.csx"
#load "..\Common\azdo.csx"
#load "..\Common\requestParameters.csx"

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public static async Task<IActionResult> Run(HttpRequest req, ILogger log, ExecutionContext executionContext)
{
    string containerName = GetParameter(req, log, "container");
    string imageId = GetParameter(req, log, "image");
    string buildId = GetParameter(req, log, "buildId");
    bool passedQa = bool.Parse(GetParameter(req, log, "passed"));
    string acrRegistry = GetParameter(req, log, "acrRegistry");
    string acrRepository = GetParameter(req, log, "acrRepository");
    string acrAuth = GetParameter(req, log, "acrAuth");
    string aciResourceGroup = GetParameter(req, log, "aciResourceGroup");
    string project = GetParameter(req, log, "project");
    string targetBranch = GetParameter(req, log, "targetBranch");
    string team = GetParameter(req, log, "team");
    string pat = GetParameter(req, log, "pat");
    string org = GetParameter(req, log, "org");

    var client = new HttpClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", pat);

    var url = $"https://dev.azure.com/{org}/{project}/_apis/build/builds/{buildId}?api-version=5.0";
    log.LogInformation($"url = {url}");
    var build = await client.GetAsync(url);
    var buildData = (JObject)JsonConvert.DeserializeObject(await build.Content.ReadAsStringAsync());
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
            error = error || DeleteAciInstance(log, executionContext.FunctionAppDirectory, aciResourceGroup, containerName);

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
        return new OkObjectResult($"Deleted container {containerName} and image {imageId}");
    }
}
