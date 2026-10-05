using System.Security.Cryptography;
using System.Text;
using TianshuDM.Domain.GameData;

namespace TianshuDM.Application.HeroAuthoring;

public static class HeroAuthoringCatalogRevision
{
    public static string Compute(GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var content = new StringBuilder();
        foreach (GameDataTable table in catalog.Tables.OrderBy(table => table.Key, StringComparer.Ordinal))
        {
            content.Append(table.Key).Append('|');
            foreach (GameDataRecord record in table.Records.OrderBy(record => record.Id))
            {
                content.Append(record.Id).Append('@').Append(record.SourceOrder).Append(':');
                foreach ((string key, IReadOnlyList<string> values) in record.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    content.Append(key).Append('=').AppendJoin(',', values).Append(';');
                }
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()))).ToLowerInvariant();
    }
}
