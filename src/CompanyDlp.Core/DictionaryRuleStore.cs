using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Logging;

namespace CompanyDlp.Core;

// Local, persisted copy of the tenant's admin-configured dictionary rules, pulled down by
// DictionaryRuleSyncWorker and read by LocalAiFileClassificationProvider on every classification -
// mirrors FileClassificationStatusStore's exact persistence pattern (same PolicyStore-derived root,
// same temp-file-then-move write).
public sealed class DictionaryRuleStore(PolicyStore policyStore, ILogger<DictionaryRuleStore> logger)
{
    private readonly object _sync = new();
    private DictionaryRulesResponse? _current;

    public DictionaryRulesResponse Get()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _current!;
        }
    }

    public void Set(DictionaryRulesResponse rules)
    {
        lock (_sync)
        {
            _current = rules;
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_current is not null) return;

        var path = GetStorePath();
        try
        {
            if (File.Exists(path))
            {
                _current = JsonSerializer.Deserialize<DictionaryRulesResponse>(File.ReadAllText(path), JsonDefaults.Options);
                if (_current is not null) return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to load the dictionary rule store; starting empty.");
        }

        _current = new DictionaryRulesResponse();
    }

    private void Save()
    {
        try
        {
            var path = GetStorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_current, JsonDefaults.Options));
            File.Move(temporary, path, true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to persist the dictionary rule store.");
        }
    }

    private string GetStorePath() => Path.Combine(GetRoot(), "dictionary-rules.json");

    private string GetRoot()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp");
    }
}
