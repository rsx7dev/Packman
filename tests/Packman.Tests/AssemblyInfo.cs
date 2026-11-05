using Xunit;

// Tests swap process-wide statics (GraphClient.Http, IntuneUploadService.AzureBlob,
// GraphClient.RetryDelayScale), so they must not run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
