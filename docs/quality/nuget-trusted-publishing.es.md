# Publicación en NuGet con trusted publishing (OIDC)

> 🇬🇧 [Read in English](nuget-trusted-publishing.md)

Aplicado con la skill `dotnet-nuget:nuget-trusted-publishing` de [dotnet/skills](https://github.com/dotnet/skills).

`.github/workflows/release.yml` ya no usa una API key de larga duración. Al hacer push de un tag `vX.Y.Z`,
el job pide a GitHub un token OIDC (`permissions: id-token: write`), la acción `NuGet/login@v1` lo cambia
en nuget.org por una API key temporal (≈1 hora) y `dotnet nuget push` la usa.

## Configuración única

1. **nuget.org** → tu cuenta → *Trusted Publishing* → *Add policy*:
   | Campo | Valor |
   |---|---|
   | Repository owner | `bluesmilecl` |
   | Repository | `AiSdk.Net` |
   | Workflow file | `release.yml` (solo el nombre, sin `.github/workflows/`) |
   | Environment | `release` |

   El dueño de la política debe ser el mismo usuario u organización dueño de los paquetes `AiSdk.Net.*` (en el primer
   publish, los paquetes nuevos quedan a nombre de ese dueño).

   > [!IMPORTANT]
   > Si el repositorio es **privado**, la política queda activa solo **7 días** hasta el primer publish exitoso (con él
   > nuget.org fija los IDs del repositorio y del dueño y la política pasa a ser permanente). Si vencen, se reinicia la
   > ventana desde la misma política.
2. **GitHub** → *Settings* → *Environments* → crear `release` (opcional: reglas de aprobación o restricción a tags `v*`).
3. En ese environment, crear el secreto `NUGET_USER` con el **nombre de perfil** de nuget.org (no el correo).

## Verificación

1. Crear un tag de prueba con versión preliminar, p. ej. `git tag v0.1.1-preview.1 && git push origin v0.1.1-preview.1`.
2. Confirmar en Actions que el paso *NuGet login (OIDC)* termina bien y que los paquetes aparecen en nuget.org.
3. Solo después de un publish exitoso, revocar la API key antigua en nuget.org y borrar el secreto `NUGET_API_KEY` de GitHub.

## Problemas frecuentes

| Síntoma | Causa |
|---|---|
| `Unable to get ACTIONS_ID_TOKEN_REQUEST_URL` | Falta `id-token: write` en `permissions`. |
| `403` / política no encontrada | Owner, repo, nombre de workflow o environment no coinciden exactamente con la política. |
| `401` en push | `NUGET_USER` es el correo o un usuario sin permisos sobre los paquetes. |
| El paquete existe | `--skip-duplicate` evita fallar si se reintenta el mismo tag. |
