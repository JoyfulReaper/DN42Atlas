namespace DN42Atlas.Registry;

public static class DomainParser
{
    public static DomainObject Parse(string path)
    {
        string? domain = null;
        var maintainers = new List<string>();
        var nameServers = new List<string>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var colonIndex = line.IndexOf(':');

            if (colonIndex <= 0)
                continue;

            var attribute = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();

            switch (attribute)
            {
                case "domain":
                    domain = value;
                    break;

                case "mnt-by":
                    maintainers.Add(value);
                    break;

                case "nserver":
                    nameServers.Add(value);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidDataException($"No domain attribute found in '{path}'.");

        return new DomainObject
        {
            Domain = domain,
            Maintainers = maintainers,
            NameServers = nameServers
        };
    }
}