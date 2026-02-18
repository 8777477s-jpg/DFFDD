namespace BoltMacro;

public sealed class UiComboItem
{
    public string Text { get; }
    public string? Id { get; }

    public UiComboItem(string text, string? id)
    {
        Text = text;
        Id = id;
    }

    public override string ToString() => Text;
}
