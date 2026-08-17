using Prism.Library;

// Консольный запуск сервиса библиотеки (unix-системы и разработка):
// контент-рут — рабочий каталог, БД — в data/, логи — в logs/.
PrismLibraryApp.Run(new WebApplicationOptions { Args = args });
