#load "..\Common\cleanup.csx"

using Microsoft.AspNetCore.Mvc;

public static async Task<IActionResult> Run(HttpRequest req, ILogger log, ExecutionContext executionContext)
{
    return await Cleanup(req, log, executionContext, true);
}