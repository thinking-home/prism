using Prism.Host;

// Обычный консольный запуск (разработка): контент-рут — рабочий каталог,
// медиа — в videos/ (или --media <папка>).
PrismHostApp.Run(new WebApplicationOptions { Args = args });
