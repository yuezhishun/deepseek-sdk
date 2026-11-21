using System.Text;
using System.Text.Json;
using static DeepSeek.Decisions.DecisionProtocol;

namespace DeepSeek.Decisions;

internal static class JevDecisionInput
{
    private const int Status = 422;
    private const int MaxImageBytes = 16 * 1024 * 1024;
    internal const int MaxExpandedBytes = 32 * 1024 * 1024;

    internal static PreparedQuestion[] Prepare(JsonElement request, string model, bool enableImages, bool enableLongContext)
    {
        Object(request, "body", Status, "model", "state", "questions", "assets");
        Model(Field(request, "model"), model, Status);
        var state = Field(request, "state");
        ValidateContent(state, "state");

        var questions = Field(request, "questions");
        Object(questions, "questions", Status);
        if (!questions.EnumerateObject().Any()) throw Invalid(Status, "questions");

        var typed = new SystemOneRequest { State = state };
        var assets = Field(request, "assets");
        if (assets.ValueKind != JsonValueKind.Undefined)
        {
            Object(assets, "assets", Status);
            if (!assets.EnumerateObject().Any()) throw Invalid(Status, "assets");
            if (!enableImages) throw Unsupported("assets");
            Vision(model, "assets");
            typed.Assets = ReadAssets(assets);
        }

        foreach (var pair in questions.EnumerateObject())
        {
            const string field = "questions";
            var question = pair.Value;
            Object(question, field, Status, "type", "instructions", "criteria");
            var type = Text(Field(question, "type"), Status, "questions.type", 16);
            var instructions = Field(question, "instructions");
            if (Present(instructions)) ValidateContent(instructions, "questions.instructions");
            var criteria = Field(question, "criteria");
            switch (type)
            {
                case "noul":
                    if (Present(criteria))
                    {
                        Object(criteria, "questions.criteria", Status, "true", "false");
                        foreach (var criterion in criteria.EnumerateObject())
                            if (Present(criterion.Value)) ValidateContent(criterion.Value, "questions.criteria");
                    }
                    if (!Present(instructions) && (!Present(criteria) || criteria.EnumerateObject().All(item => !Present(item.Value))))
                        throw Invalid(Status, "questions.instructions");
                    break;
                case "choice":
                    Object(criteria, "questions.criteria", Status);
                    if (criteria.EnumerateObject().Count() is < 1 or > 255) throw Invalid(Status, "questions.criteria");
                    foreach (var criterion in criteria.EnumerateObject())
                        if (Present(criterion.Value)) ValidateContent(criterion.Value, "questions.criteria");
                    break;
                case "score":
                    if (criteria.ValueKind != JsonValueKind.Array || criteria.GetArrayLength() is < 2 or > 10)
                        throw Invalid(Status, "questions.criteria");
                    foreach (var level in criteria.EnumerateArray()) ValidateContent(level, "questions.criteria");
                    break;
                case "box":
                    if (criteria.ValueKind != JsonValueKind.Undefined || !Present(instructions))
                        throw Invalid(Status, "questions");
                    if (!enableImages) throw Unsupported("questions.type");
                    Vision(model, "questions.type");
                    break;
                default:
                    throw Invalid(Status, "questions.type");
            }

            typed.Questions.Add(pair.Name, new SystemOneQuestion
            {
                Type = type,
                Instructions = Present(instructions) ? (object)instructions : null,
                Criteria = Present(criteria) ? (object)criteria : null,
            });
        }

        try
        {
            var bytes = (long)CompactBytes(state) + CompactBytes(questions);
            return PreparedQuestion.ForSystemOne(typed, strictProtocol: true, expandedBytes: bytes,
                enableLongContext: enableLongContext);
        }
        catch (ArgumentException) { throw Invalid(Status, "questions"); }
    }

    private static void ValidateContent(JsonElement value, string field)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw Invalid(Status, field);
        Structure(value, field, 64);
    }

    private static IDictionary<string, SystemOneAsset> ReadAssets(JsonElement assets)
    {
        var result = new Dictionary<string, SystemOneAsset>(StringComparer.Ordinal);
        long bytes = 0;
        long keyBytes = 0;
        foreach (var asset in assets.EnumerateObject())
        {
            Object(asset.Value, "assets", Status, "type", "format", "data_base64");
            var kind = OptionalText(Field(asset.Value, "type"), Status, "assets.type", 8) ?? "image";
            var format = Text(Field(asset.Value, "format"), Status, "assets.format", 8);
            if (kind != "image" || format is not ("png" or "jpeg" or "jpg") || string.IsNullOrWhiteSpace(asset.Name))
                throw Invalid(400, "assets");

            keyBytes += Encoding.UTF8.GetByteCount(asset.Name);
            if (Encoding.UTF8.GetByteCount(asset.Name) > 1024 || keyBytes > 65536)
                throw TooLarge("assets");

            var encoded = Text(Field(asset.Value, "data_base64"), Status, "assets.data_base64", int.MaxValue, 1);
            if (encoded.Length > (MaxImageBytes + 2) / 3 * 4) throw TooLarge("assets.data_base64");
            byte[] decoded;
            try { decoded = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw Invalid(400, "assets.data_base64"); }
            if (Convert.ToBase64String(decoded) != encoded) throw Invalid(400, "assets.data_base64");
            if (decoded.Length > MaxImageBytes || (bytes += decoded.Length) > MaxExpandedBytes)
                throw TooLarge("assets.data_base64");

            // The host checks the decoded pixels and dimensions. The library also rejects mismatched containers.
            var png = decoded.Length >= 8 && decoded[0] == 137 && decoded[1] == 80 && decoded[2] == 78 &&
                decoded[3] == 71 && decoded[4] == 13 && decoded[5] == 10 && decoded[6] == 26 && decoded[7] == 10;
            var jpeg = decoded.Length >= 5 && decoded[0] == 0xff && decoded[1] == 0xd8 &&
                decoded[2] == 0xff && decoded[decoded.Length - 2] == 0xff && decoded[decoded.Length - 1] == 0xd9;
            if (format == "png" ? !png : !jpeg) throw Invalid(400, "assets.format");
            result.Add(asset.Name, new SystemOneAsset { Type = kind, Format = format, DataBase64 = encoded });
        }
        return result;
    }

    internal static DecisionProtocolException TooLarge(string field) =>
        new(413, "request_too_large", "请求超过协议体积上限。", field);
}
