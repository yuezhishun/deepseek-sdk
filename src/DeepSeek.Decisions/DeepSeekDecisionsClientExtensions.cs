namespace DeepSeek.Decisions;

public static class DeepSeekDecisionsClientExtensions
{
    public static DecisionsClient GetDecisionsClient(this DeepSeekClient client, string model)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        return new DecisionsClient(client.GetChatClient(model));
    }
}
