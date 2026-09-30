using Prism.Library;

// SCM запускает службы из System32. Настройки, относительные пути БД и веб-клиент
// всегда ищем рядом с exe, в том числе при диагностическом запуске из консоли.
PrismLibraryApp.Run(
    new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    },
    builder => builder.Host.UseWindowsService(options => options.ServiceName = "PrismLibrary"));
