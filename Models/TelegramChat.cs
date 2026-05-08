namespace EchoVault.Models;

public record TelegramChat(string Name, long Id)
{
    public override string ToString() => Name;
}
