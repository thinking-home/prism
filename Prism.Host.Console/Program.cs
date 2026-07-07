using Prism.Host;

// Обычный консольный запуск (разработка): контент-рут — рабочий каталог,
// плагины кладутся сборкой в plugins/, медиа — в videos/ (или --media <папка>).
PrismHostApp.Run(new WebApplicationOptions { Args = args });
