using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;

public static async Task<bool> DeleteAcrImage(ILogger log, string authHeader, string registry, string repository, string imageId)
{
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