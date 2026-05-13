namespace Replixer.Models;

public record TelegramChat(string Name, long Id, int? TopicId = null)
{
    public override string ToString() => Name;
}
