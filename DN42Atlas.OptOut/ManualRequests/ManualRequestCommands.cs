using System.Globalization;

namespace DN42Atlas.OptOut.ManualRequests;

public static class ManualRequestCommands
{
    public static async Task<int> ExecuteAsync(string[] args, ManualRequestStore store, TextWriter output)
    {
        if (args.Length == 1 && args[0] == "manual-requests")
        {
            await output.WriteLineAsync("ID\tCreatedUtc\tRequestType\tResource\tContact\tStatus");
            foreach (var request in await store.ReadAsync())
                await output.WriteLineAsync($"{request.Id}\t{request.CreatedUtc:O}\t{request.RequestType}\t{request.Resource}\t{request.Contact}\t{request.Status}");
            return 0;
        }
        if (args.Length == 2 && args[0] == "manual-request" && long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            var request = (await store.ReadAsync(id)).SingleOrDefault();
            if (request == null) { await output.WriteLineAsync("Request not found."); return 1; }
            await output.WriteLineAsync($"ID: {request.Id}\nCreatedUtc: {request.CreatedUtc:O}\nRequestType: {request.RequestType}\nResource: {request.Resource}\nContact: {request.Contact}\nStatus: {request.Status}\nReviewedUtc: {request.ReviewedUtc:O}\nMessage:\n{request.Message}");
            return 0;
        }
        if (args.Length == 3 && args[0] == "manual-request-status" && TryId(args[1], out var statusId) &&
            Enum.GetNames<ManualRequestStatus>().Contains(args[2], StringComparer.Ordinal))
        {
            var changed = await store.SetStatusAsync(statusId, Enum.Parse<ManualRequestStatus>(args[2]));
            await output.WriteLineAsync(changed ? $"Request {statusId} status set to {args[2]}." : "Request not found.");
            return changed ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "manual-request-delete" && TryId(args[1], out var deleteId))
        {
            var deleted = await store.DeleteAsync(deleteId);
            await output.WriteLineAsync(deleted ? $"Request {deleteId} deleted." : "Request not found.");
            return deleted ? 0 : 1;
        }
        await output.WriteLineAsync("Usage: manual-requests | manual-request <id> | manual-request-status <id> <Pending|Reviewed|Resolved|Rejected> | manual-request-delete <id>");
        return 2;
    }

    private static bool TryId(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
