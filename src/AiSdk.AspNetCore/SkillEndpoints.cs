using AiSdk.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.AspNetCore;

/// <summary>Endpoints de solo lectura para explorar las skills (p. ej. un menú de comandos <c>/skill</c> en la UI).</summary>
public static class SkillEndpoints
{
    /// <summary>
    /// <list type="bullet">
    /// <item><c>GET {prefix}</c>: nombre, descripción y si el usuario/modelo puede invocarla</item>
    /// <item><c>GET {prefix}/{name}</c>: SKILL.md completo y lista de archivos</item>
    /// <item><c>GET {prefix}/{name}/files/{**path}</c>: un archivo de la skill</item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapSkills(this IEndpointRouteBuilder endpoints, string prefix = "/skills", SkillLibrary? library = null)
    {
        var group = endpoints.MapGroup(prefix);
        SkillLibrary Library(HttpContext http) => library ?? http.RequestServices.GetRequiredService<SkillLibrary>();

        group.MapGet("/", (HttpContext http, CancellationToken _) => Results.Ok(Library(http).Skills.Select(s => new
        {
            name = s.Name,
            description = s.Description,
            modelInvocable = s.ModelInvocable,
            license = s.License,
            files = s.Files.List().Count,
        })));

        group.MapGet("/{name}", (HttpContext http, string name) => Library(http).Get(name) is { } skill
            ? Results.Ok(new { name = skill.Name, description = skill.Description, markdown = skill.ToMarkdown(), files = skill.Files.List() })
            : Results.NotFound());

        group.MapGet("/{name}/files/{**path}", async (HttpContext http, string name, string path) =>
        {
            if (Library(http).Get(name) is not { } skill)
            {
                return Results.NotFound();
            }

            string normalized;
            try
            {
                normalized = SkillFiles.NormalizePath(path);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }

            var data = await skill.Files.ReadAsync(normalized, http.RequestAborted).ConfigureAwait(false);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return data is null ? Results.NotFound() : Results.File(data, normalized.EndsWith(".md", StringComparison.Ordinal) ? "text/markdown; charset=utf-8" : "application/octet-stream", Path.GetFileName(normalized));
        });

        return group;
    }
}
