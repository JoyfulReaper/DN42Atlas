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
            await output.WriteLineAsync($"ID: {request.Id}\nCreatedUtc: {request.CreatedUtc:O}\nRequestType: {request.RequestType}\nResource: {request.Resource}\nContact: {request.Contact}\nStatus: {request.Status}\nMessage:\n{request.Message}");
            return 0;
        }
        await output.WriteLineAsync("Usage: manual-requests | manual-request <id>");
        return 2;
    }
}
