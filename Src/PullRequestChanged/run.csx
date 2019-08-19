#r "Newtonsoft.Json"

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static async Task<IActionResult> Run(HttpRequest req, ILogger log, ExecutionContext executionContext)
{
    string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
    log.LogInformation(requestBody);
    var data = (JObject)JsonConvert.DeserializeObject(requestBody);
    log.LogInformation($"PR {data["resource"]["pullRequestId"].ToString()} changed");

    var status = data["resource"]["status"].ToString();
    log.LogInformation($"status = {status}");

    if (status != "abandoned")
    {
        var reviewComplete = true;
        var reviewers = (JArray)data["resource"]["reviewers"];
        foreach (var reviewer in reviewers)
        {
            var vote = int.Parse(reviewer["vote"].ToString());
            log.LogInformation($"{reviewer["displayName"].ToString()} voted {vote}");
            if (vote <= 0)
            {
                reviewComplete = false;
            }
        }

        if (reviewComplete)
        {
            // queue up build for this branch...
            var buildId = req.Headers["BuildId"];
            var pat = req.Headers["pat"];
            var sourceBranch = data["resource"]["sourceRefName"].ToString();  // includes refs/heads/
            var organization = data["resourceContainers"]["project"]["baseUrl"].ToString();
            organization = organization.Substring(0, organization.IndexOf("."));
            organization = organization.Substring(organization.LastIndexOf("/")+1);
            var project = data["resource"]["repository"]["project"]["name"].ToString();
            log.LogInformation($"azdo pat = {pat}");
            log.LogInformation($"queuing build for {project}/{sourceBranch}/{buildId}");
            log.LogInformation($"organization = {organization}");
            log.LogInformation($"project = {project}");

            var body = $@"{{
                ""definition"": {{
                    ""id"": ""{buildId}""
                }},
                ""sourceBranch"": ""{sourceBranch}""
            }}";
            log.LogInformation(body);

            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", pat);

            var url = $"https://dev.azure.com/{organization}/{project}/_apis/build/builds?api-version=5.0".Replace(" ", "%20");
            log.LogInformation(url);
            var response = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
            var content = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var json = (JObject)JsonConvert.DeserializeObject(content);
                log.LogInformation($"Build # {json["buildNumber"].ToString()} (id = {json["id"].ToString()}) queued");
            }
            else
            {
                log.LogInformation($"Received {response.StatusCode} status code: {content}");
            }
        }
        else
        {
            log.LogInformation("PR review is incomplete");
        }
    }

    return new OkObjectResult("Done!");
}