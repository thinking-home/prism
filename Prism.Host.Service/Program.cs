using Microsoft.Extensions.Hosting.WindowsServices;
using Prism.Host;

// Запуск службой Windows. У службы рабочий каталог — System32, поэтому
// контент-рут явно указываем на папку приложения. При обычном запуске exe
// (не службой) UseWindowsService — no-op.
PrismHostApp.Run(
    new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
    },
    builder => builder.Host.UseWindowsService());
