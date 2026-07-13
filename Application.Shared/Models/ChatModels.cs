namespace Application.Shared.Models;

public class DatasetSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
}

public class TableReference
{
    public string Id { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string DatasetName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<TableColumn> Columns { get; set; } = new();
    public List<Dictionary<string, object>> SampleData { get; set; } = new();
}

public class TableColumn
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
}

public class TableSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string DatasetName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public List<TableColumn> Columns { get; set; } = new();
}

/// <summary>
/// Configuration for Azure OpenAI service integration
/// </summary>
public class AzureOpenAIConfiguration
{
    /// <summary>
    /// Azure OpenAI endpoint URL
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;
    
    /// <summary>
    /// API key for authentication
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    
    /// <summary>
    /// Name of the deployed model
    /// </summary>
    public string DeploymentName { get; set; } = string.Empty;
    
    /// <summary>
    /// API version to use
    /// </summary>
    public string ApiVersion { get; set; } = "2024-08-01-preview";
    
    /// <summary>
    /// Maximum number of tokens to generate
    /// </summary>
    public int MaxTokens { get; set; } = 1000;
    
    /// <summary>
    /// Temperature for response generation (0.0 to 1.0)
    /// </summary>
    public double Temperature { get; set; } = 0.7;
    
    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Maximum retry attempts for failed requests
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;
}
