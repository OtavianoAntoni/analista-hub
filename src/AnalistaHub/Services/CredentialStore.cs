using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AnalistaHub.Services;

/// <summary>
/// Credenciais digitadas na própria interface. Ficam cifradas com DPAPI (escopo do usuário
/// Windows) em %LOCALAPPDATA%\AnalistaHub\credenciais.dat — quem copiar o arquivo para outra
/// máquina ou outro usuário não consegue ler. Nunca vão para log, histórico ou manifesto.
/// </summary>
public static class CredentialStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, Dictionary<string, string>> _cache = new();
    private static bool _loaded;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "credenciais.dat");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(FilePath))
                {
                    var protectedBytes = File.ReadAllBytes(FilePath);
                    var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                    var json = Encoding.UTF8.GetString(plain);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
                             ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log("credenciais: falha ao ler (" + ex.GetType().Name + "); começando vazio");
                _cache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            }
            _loaded = true;
        }
    }

    public static string Get(string toolId, string key)
    {
        EnsureLoaded();
        lock (Gate)
            return _cache.TryGetValue(toolId, out var fields) && fields.TryGetValue(key, out var value) ? value : "";
    }

    public static Dictionary<string, string> GetAll(string toolId)
    {
        EnsureLoaded();
        lock (Gate)
            return _cache.TryGetValue(toolId, out var fields)
                ? new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static void Set(string toolId, string key, string value)
    {
        EnsureLoaded();
        lock (Gate)
        {
            if (!_cache.TryGetValue(toolId, out var fields))
            {
                fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _cache[toolId] = fields;
            }
            if (string.IsNullOrEmpty(value)) fields.Remove(key);
            else fields[key] = value;

            if (fields.Count == 0) _cache.Remove(toolId);
            Save();
        }
    }

    public static bool HasAll(string toolId, IEnumerable<string> keys)
    {
        EnsureLoaded();
        lock (Gate)
            return _cache.TryGetValue(toolId, out var fields) &&
                   keys.All(k => fields.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));
    }

    private static void Save()
    {
        try
        {
            AppPaths.Ensure();
            var json = JsonSerializer.Serialize(_cache);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, protectedBytes);
        }
        catch (Exception ex)
        {
            AppPaths.Log("credenciais: falha ao gravar (" + ex.GetType().Name + ")");
        }
    }

    public static void Clear(string toolId)
    {
        EnsureLoaded();
        lock (Gate)
        {
            _cache.Remove(toolId);
            Save();
        }
    }
}
