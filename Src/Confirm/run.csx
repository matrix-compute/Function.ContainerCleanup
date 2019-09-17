#load "..\Common\requestParameters.csx"

using System;
using System.Net;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

public static IActionResult Run(HttpRequest req, ILogger log)
{
    string title = GetParameter(req, log, "title");
    string container = GetParameter(req, log, "container");
    string image = GetParameter(req, log, "image");
    string buildId = GetParameter(req, log, "buildId");
    bool passedQa = bool.Parse(GetParameter(req, log, "passed"));
    string acrRegistry = GetParameter(req, log, "acrRegistry");
    string acrRepository = GetParameter(req, log, "acrRepository");
    string acrAuth = GetParameter(req, log, "acrAuth");
    string aciResourceGroup = GetParameter(req, log, "aciResourceGroup");
    string project = GetParameter(req, log, "project");
    string targetBranch = GetParameter(req, log, "targetBranch");
    string team = GetParameter(req, log, "team");
    string callbackUrl = GetParameter(req, log, "callbackUrl");  // should include the function auth code as a query string parameter
    string pat = GetParameter(req, log, "pat");
    string org = GetParameter(req, log, "org");
    
    callbackUrl = $"{callbackUrl}&container={container}&image={image}&buildId={buildId}&passed={passedQa}&acrRegistry={acrRegistry}&acrRepository={acrRepository}&acrAuth={acrAuth}&aciResourceGroup={aciResourceGroup}&project={project}&targetBranch={targetBranch}&team={team}&pat={pat}&org={org}"
        .Replace(" ", "%20");
    log.LogInformation($"callback url: {callbackUrl}");

    var html = new StringBuilder();
    html.Append($"<html><head><title>{title}</title></head>");
    html.Append($"<form action=\"{callbackUrl}\" method=\"post\"><body><h1>{title}</h1>");
    if (passedQa)
    {
        html.Append($"<p>Are you sure you want to delete container <strong>{container}</strong></p> and image <strong>{image}</strong></p>");
        if (callbackUrl.Contains("Create"))
        {
            html.Append($"<p>(this will automatically create a Pull Request to merge the feature branch into {targetBranch})</p>");
        }
        else
        {
            html.Append($"<p>(this will automatically complete the open Pull Request and merge the feature branch into {targetBranch})</p>");
        }
    }
    else
    {
        html.Append("<p>This will create a Work Item (Bug) and open it in the browser</p>");
    }
    html.Append("<input type=\"submit\" value=\"DO IT!!!\" /></body></form></html>");

    log.LogInformation("done");
    // this is essentially a confirmation dialog presented as a web page
    return new ContentResult { Content = html.ToString(), ContentType = "text/html" };
}
