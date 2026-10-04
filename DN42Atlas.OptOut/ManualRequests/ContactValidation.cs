namespace DN42Atlas.OptOut.ManualRequests;

public static class ContactValidation
{
    public static bool TryParse(IFormCollection form, out RequestInput? input)
    {
        input = null;
        var type = form["RequestType"].ToString().Trim();
        if (!Enum.GetNames<ManualRequestType>().Contains(type, StringComparer.Ordinal)) return false;
        input = new(form["Resource"].ToString().Trim(), form["Contact"].ToString().Trim(),
            Enum.Parse<ManualRequestType>(type), form["Message"].ToString().Trim());
        return IsValid(input);
    }

    public static bool IsValid(RequestInput input) => Enum.IsDefined(input.RequestType) &&
        Text(input.Resource, 255, false) && Text(input.Contact, 255, false) && Text(input.Message, 2000, true);

    private static bool Text(string value, int limit, bool multiline) => value.Length is > 0 && value.Length <= limit &&
        !string.IsNullOrWhiteSpace(value) && value.All(c => !char.IsControl(c) || (multiline && c is '\r' or '\n' or '\t'));
}
