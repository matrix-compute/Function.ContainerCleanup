using System.Web;

public static string GetParameter(HttpRequest req, ILogger log, string parameterName)
{
    string result = req.Query[parameterName];
    if (string.IsNullOrEmpty(result))
    {
        log.LogInformation($"{parameterName} not found in query string");
    }
    else
    {
        log.LogInformation($"{parameterName} = {result}");
    }
    return result;
}