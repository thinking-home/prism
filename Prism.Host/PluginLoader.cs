using System.Reflection;
using System.Runtime.Loader;
using Prism.Abstractions;

namespace Prism.Host;

/// <summary>
/// Минимальный загрузчик плагинов стандартными средствами .NET. Список сборок
/// задаётся в appsettings (секция "Plugins"). Каждая грузится из
/// <c>plugins/&lt;имя&gt;/&lt;имя&gt;.dll</c> в собственный <see cref="AssemblyLoadContext"/>
/// (зависимости плагина резолвятся из его папки; контракт и фреймворк — из хоста),
/// после чего в ней ищутся реализации <see cref="IPrismModule"/>.
/// </summary>
public static class PluginLoader
{
    public static IReadOnlyList<IPrismModule> Load(IConfiguration config, string contentRoot, ILogger logger)
    {
        var names = config.GetSection("Plugins").Get<string[]>() ?? [];
        var modules = new List<IPrismModule>();

        foreach (var name in names)
        {
            var dll = Path.Combine(contentRoot, "plugins", name, name + ".dll");
            if (!File.Exists(dll))
            {
                logger.LogWarning("Плагин {name} не найден: {path}", name, dll);
                continue;
            }

            try
            {
                var ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadFromAssemblyPath(dll);
                foreach (var type in asm.GetTypes())
                {
                    if (!typeof(IPrismModule).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                        continue;
                    if (Activator.CreateInstance(type) is IPrismModule module)
                    {
                        modules.Add(module);
                        logger.LogInformation("Загружен плагин: {name} ({type})", name, type.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Не удалось загрузить плагин {name}", name);
            }
        }

        return modules;
    }

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Контракт берём из основного контекста, иначе типы IPrismModule /
            // IMediaMetaSource не совпадут между хостом и плагином.
            if (name.Name == "Prism.Abstractions")
                return null;

            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
