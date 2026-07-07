namespace LocalOpsBot.Data;

public sealed class DataOptions
{
    public string DatabasePath { get; set; } = "%ProgramData%/LocalOpsBot/data/localops.db";
}
