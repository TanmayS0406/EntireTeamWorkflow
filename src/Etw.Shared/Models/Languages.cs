namespace Etw.Shared.Models;

/// <summary>A backend language/framework or a UI framework offered in the dropdowns.</summary>
/// <param name="Id">Stable id stored on the project.</param>
/// <param name="Name">Label shown in the dropdown.</param>
/// <param name="Hint">What the agent is told to build with (frameworks, layout, test tooling).</param>
/// <param name="Tools">Command-line tools the agent may run without asking (becomes Claude Code Bash allow rules).</param>
/// <param name="AzureRuntime">App Service Linux runtime used when deploying, or null when there is no built-in runtime.</param>
public sealed record LanguageOption(string Id, string Name, string Hint, string[] Tools, string? AzureRuntime);

public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageOption> Backends { get; } =
    [
        new("csharp-aspnet", "C# · ASP.NET Core (.NET 10)",
            "C# on .NET 10 with ASP.NET Core minimal APIs, EF Core with SQLite for storage, OpenAPI via Microsoft.AspNetCore.OpenApi, xUnit tests (WebApplicationFactory for API tests).",
            ["dotnet"], "DOTNETCORE|10.0"),
        new("java-spring", "Java · Spring Boot",
            "Java 21 with Spring Boot 3 (Spring Web, Spring Data JPA with H2/SQLite-compatible storage), springdoc-openapi, JUnit 5 tests. Use Maven (use the wrapper if you create one).",
            ["mvn", "mvnw", "./mvnw", "java", "javac", "gradle", "gradlew", "./gradlew"], "JAVA|21-java21"),
        new("python-fastapi", "Python · FastAPI",
            "Python 3.12 with FastAPI, Pydantic v2, SQLAlchemy with SQLite, pytest. Use a virtual environment inside the backend folder and pin dependencies in requirements.txt.",
            ["python", "python3", "py", "pip", "pip3", "pytest", "uvicorn"], "PYTHON|3.12"),
        new("node-express", "Node.js · TypeScript (Express)",
            "Node.js 22 with TypeScript and Express, zod for validation, better-sqlite3 or an in-memory store, Vitest/Jest tests with supertest.",
            ["node", "npm", "npx"], "NODE|22-lts"),
        new("go-gin", "Go · Gin",
            "Go 1.23+ with the Gin web framework, database/sql with SQLite (modernc.org/sqlite, no cgo), table-driven tests with testing and httptest.",
            ["go", "gofmt"], null),
        new("kotlin-ktor", "Kotlin · Ktor",
            "Kotlin with Ktor server (Netty), kotlinx.serialization, Exposed with SQLite, Gradle (Kotlin DSL), Ktor test host for tests.",
            ["gradle", "gradlew", "./gradlew", "java"], "JAVA|21-java21"),
        new("rust-axum", "Rust · Axum",
            "Rust (stable) with Axum, Tokio, serde, sqlx with SQLite, tests with cargo test and tower::ServiceExt.",
            ["cargo", "rustc"], null),
        new("php-laravel", "PHP · Laravel",
            "PHP 8.3 with Laravel 11, Eloquent with SQLite, Pest or PHPUnit tests, API resources and form requests.",
            ["php", "composer"], "PHP|8.3"),
    ];

    public static IReadOnlyList<LanguageOption> UiFrameworks { get; } =
    [
        new("blazor", "Blazor (C#)",
            "Blazor on .NET 10 (standalone WebAssembly app) with typed HttpClient services generated from the API contract, componentised pages, bUnit tests for key components.",
            ["dotnet"], null),
        new("react-ts", "React + TypeScript (Vite)",
            "React 19 with TypeScript, built with Vite, React Router, TanStack Query for server state, Vitest + Testing Library.",
            ["node", "npm", "npx"], "NODE|22-lts"),
        new("angular", "Angular",
            "Angular (latest) with standalone components, signals, the Angular router and HttpClient, Jasmine/Karma or Jest tests. Use the Angular CLI through npx.",
            ["node", "npm", "npx", "ng"], "NODE|22-lts"),
        new("vue", "Vue 3 + TypeScript (Vite)",
            "Vue 3 with the Composition API and TypeScript, Vite, Vue Router, Pinia, Vitest + Vue Test Utils.",
            ["node", "npm", "npx"], "NODE|22-lts"),
        new("svelte", "SvelteKit",
            "SvelteKit with TypeScript (static adapter), Svelte 5 runes, Vitest.",
            ["node", "npm", "npx"], "NODE|22-lts"),
        new("nextjs", "Next.js (React, TypeScript)",
            "Next.js (App Router) with TypeScript, server components where useful, Tailwind CSS, Vitest/Jest.",
            ["node", "npm", "npx"], "NODE|22-lts"),
        new("html", "Plain HTML · CSS · JavaScript",
            "Dependency-free HTML, modern CSS and ES modules, no build step; small JS unit tests with node:test.",
            ["node", "npm", "npx"], "NODE|22-lts"),
    ];

    public static LanguageOption Backend(string? id) => Backends.FirstOrDefault(b => b.Id == id) ?? Backends[0];

    public static LanguageOption Ui(string? id) => UiFrameworks.FirstOrDefault(u => u.Id == id) ?? UiFrameworks[0];
}
