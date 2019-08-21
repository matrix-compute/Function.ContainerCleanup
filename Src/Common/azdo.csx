#r "Newtonsoft.Json"

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static async Task<string> AzdoCreateBug(ILogger log, HttpClient client, string project, string team, string buildId, string projectId, string repositoryId, string sourceBranch, string containerName, string org)
{
    // find current iteration
    var result = await client.GetAsync($"https://dev.azure.com/{org}/{project}/{team}/_apis/work/teamsettings/iterations?$timeframe=current&api-version=5.0");
    var data = (JArray)((JObject)JsonConvert.DeserializeObject(await result.Content.ReadAsStringAsync()))["value"];
    string currentIteration = (data.First()["path"]).ToString().Replace(@"\", @"\\");  // have to escape the backslash
    log.LogInformation($"current iteration for {team.Replace("%20", " ")} = {currentIteration}");

    var workitems = await client.GetAsync($"https://dev.azure.com/{org}/{project}/_apis/build/builds/{buildId}/workitems?api-version=5.0");
    var workitemsData = (JArray)((JObject)JsonConvert.DeserializeObject(await workitems.Content.ReadAsStringAsync()))["value"];
    var workitemLinks = workitemsData.Select(x => x["id"]);
    log.LogInformation($"{workitemLinks.Count()} work item links found");

    var wi = new StringBuilder();
    foreach (var id in workitemLinks)
    {
        var workitem = await client.GetAsync($"https://dev.azure.com/{org}/{project}/_apis/wit/workitems?ids={id}&api-version=5.0");
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
        {{
            ""op"": ""add"",
            ""path"": ""/fields/System.IterationPath"",
            ""value"": ""{currentIteration}""
        }},
        {{
            ""op"": ""add"",
            ""path"": ""/fields/PANDORAIterative.UnplannedWork"",
            ""value"": ""true""
        }},
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

    var response = await client.PostAsync($"https://dev.azure.com/{org}/{project}/_apis/wit/workitems/$Bug?api-version=5.0",
        new StringContent(body, Encoding.UTF8, "application/json-patch+json"));
    var content = (JObject)JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync());
    log.LogInformation($"Bug {content["id"].ToString()} created");

    return content["_links"]["html"]["href"].ToString();
}

public static async Task AzdoCreatePullRequest(ILogger log, HttpClient client, string project, string repositoryId, string sourceBranch, string targetBranch, string org)
{
    log.LogInformation($"creating PR for {sourceBranch} to {targetBranch}");

    var body = $@"{{
        ""sourceRefName"": ""refs/heads/{sourceBranch}"",
        ""targetRefName"": ""refs/heads/{targetBranch}"",
        ""title"": ""QA passed"",
        ""description"": ""Created automagically""
    }}";
    log.LogInformation(body);
    var response = await client.PostAsync($"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repositoryId}/pullrequests?api-version=5.0",
        new StringContent(body, Encoding.UTF8, "application/json"));
    var content = (JObject)JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync());
    log.LogInformation($"Pull request {content["pullRequestId"].ToString()} created");
}

public static async Task AzdoCompletePullRequest(ILogger log, HttpClient client, string project, string repositoryId, string sourceBranch, string targetBranch, string org)
{
    var pr = await GetActivePR(log, client, project, sourceBranch, org);
    if (string.IsNullOrEmpty(pr))
    {
        log.LogInformation($"cannot complete PR (no active PR found for {sourceBranch})");
    }
    else
    {
        log.LogInformation($"completing PR {pr} ({sourceBranch} to {targetBranch})");

        // set 'succeeded' status
        var body = $@"{{
            ""state"": ""succeeded"",
            ""description"": ""QA testing passed"",
            ""context"": {{
                ""name"": ""passed"",
                ""genre"": ""qa""
            }}
        }}";
        //"targetUrl": "http://fabrikam-fiber-inc.com/CI/builds/1"
        log.LogInformation(body);
        var response = await client.PostAsync($"https://dev.azure.com/{org}/_apis/git/repositories/{repositoryId}/pullRequests/{pr}/statuses?api-version=5.0-preview.1",
            new StringContent(body, Encoding.UTF8, "application/json"));
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