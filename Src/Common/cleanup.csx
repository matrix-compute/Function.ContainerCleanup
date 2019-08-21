#r "Newtonsoft.Json"
#r "Microsoft.Extensions.Configuration"
#r "Microsoft.Extensions.Configuration.Abstractions"
#r "Microsoft.Extensions.Configuration.EnvironmentVariables"
#r "Microsoft.Extensions.Configuration.FileExtensions"

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
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static async Task<IActionResult> Cleanup(HttpRequest req, ILogger log, ExecutionContext executionContext, bool completePr)
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
    string organization = GetParameter(req, log, "organization");

    if (containerName == null || imageId == null)
    {
        return new BadRequestObjectResult("Please pass a container reference & image tag on the query string or in the request body");
    }
    else
    {
        var error = false;
        var redirect = string.Empty;

        try
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", pat);

            var build = await client.GetAsync($"https://dev.azure.com/{organization}/{project}/_apis/build/builds/{buildId}?api-version=5.0");
            var buildData = (JObject)JsonConvert.DeserializeObject(await build.Content.ReadAsStringAsync());
            var repositoryId = buildData["repository"]["id"].ToString();
            log.LogInformation($"repository id = {repositoryId}");
            var sourceBranch = buildData["sourceBranch"].ToString();  // includes "refs/heads/"
            sourceBranch = sourceBranch.Substring(sourceBranch.LastIndexOf("/")+1);
            log.LogInformation($"source branch = {sourceBranch}");

            if (passedQa)
            {
                // Azure Container Registry (ACR)
                error = await DeleteAcrImage(log, acrAuth, acrRegistry, acrRepository, imageId);

                // Azure Container Instances (ACI)
                error = error || DeleteAciInstance(log, executionContext.FunctionAppDirectory, aciResourceGroup, containerName);

                if (completePr)
                {
                    await AzdoCompletePullRequest(log, client, project, repositoryId, sourceBranch, targetBranch);
                }
                else
                {
                    await AzdoCreatePullRequest(log, client, project, repositoryId, sourceBranch, targetBranch);
                }
            }
            else
            {
                log.LogInformation("creating bug");
                var projectId = buildData["definition"]["project"]["id"].ToString();
                redirect = await AzdoCreateBug(log, client, project, team, buildId, projectId, repositoryId, sourceBranch, containerName);
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
        else
         if (!string.IsNullOrEmpty(redirect))
        {
            return new RedirectResult(redirect);
        }
        else
        {
            return new OkObjectResult($"Deleted container {containerName} and image {imageId}");
        }
    }
}