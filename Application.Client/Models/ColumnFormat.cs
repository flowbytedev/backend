namespace Application.Client.Models;

public enum FormatType
{
    Text,
    Number,
    Currency,
    Percentage,
    Date,
    DateTime,
    Boolean
}

public class ColumnFormat
{
    public FormatType Type { get; set; } = FormatType.Text;
    public int? DecimalPlaces { get; set; }
    public string? CurrencySymbol { get; set; }
    public string? DateFormat { get; set; }
    public string? Prefix { get; set; }
    public string? Suffix { get; set; }
    public bool MultiplyBy100 { get; set; } = false;
}
