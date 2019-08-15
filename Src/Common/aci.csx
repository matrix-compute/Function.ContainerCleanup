using System;
using System.Text;
using Microsoft.Extensions.Primitives;
using Microsoft.Azure.Management.Fluent;

public static bool DeleteAciInstance(ILogger log, string functionAppDirectory, string resourceGroup, string containerName)
{
    // for ACI delete using Azure Management classes & container name
    string authFilePath = $@"{functionAppDirectory}\Common\my.azureauth";
    try
    {
        //to generate credentials file: "az ad sp create-for-rbac --sdk-auth > my.azureauth"
        log.LogInformation($"Authenticating with Azure using credentials in file at {authFilePath}");

        IAzure azure = Azure.Authenticate(authFilePath).WithDefaultSubscription();
        var sub = azure.GetCurrentSubscription();
        log.LogInformation($"Authenticated with subscription '{sub.DisplayName}' (ID: {sub.SubscriptionId})");

        var containerId = $"/subscriptions/{sub.SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.ContainerInstance/containerGroups/{containerName}";  
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