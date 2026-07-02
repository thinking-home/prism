using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Prism.Abstractions;

/// <summary>
/// Модуль-плагин Prism. Хост находит реализации в сборках, перечисленных в
/// appsettings (секция "Plugins"), и вызывает эти методы. Без модулей ядро
/// работает штатно — плагины лишь добавляют функциональность.
/// </summary>
public interface IPrismModule
{
    /// <summary>Регистрация сервисов модуля в контейнере DI хоста.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Регистрация HTTP-эндпоинтов модуля.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
