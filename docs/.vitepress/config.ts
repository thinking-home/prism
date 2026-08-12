import { defineConfig } from "vitepress";

// Сайт документации Prism. Собирается в статику и уезжает на GitHub Pages
// (см. .github/workflows/docs.yml). Живёт в корне отдельного домена, поэтому
// base не задаём — пути к ассетам абсолютные от корня.
export default defineConfig({
  lang: "ru-RU",
  title: "Prism",
  description: "Сетевой медиа-плеер: сервер, веб-клиент и плеер для Android TV",
  cleanUrls: true,
  lastUpdated: true,

  // Адреса локальных сервисов в тексте — не ссылки на внешние страницы,
  // проверять их нечем: при сборке никакого localhost нет.
  ignoreDeadLinks: [/^https?:\/\/localhost/],

  themeConfig: {
    nav: [
      { text: "Руководство", link: "/guide/what-is-prism" },
      { text: "API", link: "/api/http" },
      { text: "GitHub", link: "https://github.com/thinking-home/prism" },
    ],

    // Разделы наполняются по мере переноса содержимого из README.
    sidebar: [
      {
        text: "Руководство",
        items: [
          { text: "Что такое Prism", link: "/guide/what-is-prism" },
          { text: "Быстрый старт", link: "/guide/quick-start" },
        ],
      },
      {
        text: "API",
        items: [
          { text: "HTTP API хоста", link: "/api/http" },
          { text: "MQTT-контракт плеера", link: "/api/mqtt" },
        ],
      },
    ],

    socialLinks: [{ icon: "github", link: "https://github.com/thinking-home/prism" }],

    // Поиск по статике, без внешних сервисов.
    search: { provider: "local" },

    outline: { label: "На этой странице" },
    docFooter: { prev: "Назад", next: "Дальше" },
    darkModeSwitchLabel: "Оформление",
    lightModeSwitchTitle: "Светлая тема",
    darkModeSwitchTitle: "Тёмная тема",
    sidebarMenuLabel: "Разделы",
    returnToTopLabel: "Наверх",
    lastUpdatedText: "Обновлено",

    editLink: {
      pattern: "https://github.com/thinking-home/prism/edit/main/docs/:path",
      text: "Предложить правку",
    },

    footer: {
      message: "Опубликовано под лицензией MIT",
      copyright: "© Prism",
    },
  },
});
