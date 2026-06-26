#Requires -Version 5.1
<#
.SYNOPSIS
    Guía paso a paso para traer cambios de velopack/velopack y combinarlos con los locales.

.DESCRIPTION
    Este script sincroniza la rama actual con upstream/develop del repositorio original:
    https://github.com/velopack/velopack

    Flujo recomendado:
      1. Verificar requisitos y estado del repositorio
      2. Configurar el remoto 'upstream' si no existe
      3. Guardar cambios locales sin commitear (stash) si es necesario
      4. Descargar cambios de upstream
      5. Mostrar resumen de commits nuevos
      6. Combinar con merge o rebase (a elección del usuario)
      7. Restaurar el stash y opcionalmente publicar en origin

.PARAMETER UpstreamUrl
    URL del repositorio original. Por defecto: https://github.com/velopack/velopack.git

.PARAMETER UpstreamBranch
    Rama de upstream a sincronizar. Por defecto: develop

.PARAMETER Strategy
    Estrategia de combinación: Merge, Rebase o preguntar interactivamente.

.PARAMETER SkipPush
    No ofrecer publicar los cambios en los remotos al finalizar.

.PARAMETER NonInteractive
    Ejecuta con valores por defecto (merge, sin push). Útil para CI o automatización.

.EXAMPLE
    .\scripts\sync-upstream.ps1

.EXAMPLE
    .\scripts\sync-upstream.ps1 -Strategy Rebase -SkipPush
#>
[CmdletBinding()]
param(
    [string] $UpstreamUrl = "https://github.com/velopack/velopack.git",
    [string] $UpstreamBranch = "develop",
    [ValidateSet("Merge", "Rebase", "Ask")]
    [string] $Strategy = "Ask",
    [switch] $SkipPush,
    [switch] $NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Git escribe progreso en stderr; con Stop eso dispara un error falso aunque exit code sea 0.
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::InputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
}
catch {
    # Consola no interactiva; ignorar.
}

$UpstreamRemoteName = "upstream"
$StashMessage = "sync-upstream.ps1 - cambios locales antes de sincronizar con upstream"

function Write-Title([string] $Text) {
    Write-Host ""
    Write-Host ("=" * 72) -ForegroundColor Cyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ("=" * 72) -ForegroundColor Cyan
    Write-Host ""
}

function Write-Step([int] $Number, [string] $Text) {
    Write-Host ""
    Write-Host "Paso $Number`: $Text" -ForegroundColor Yellow
    Write-Host ("-" * 60) -ForegroundColor DarkGray
}

function Write-Info([string] $Text) {
    Write-Host "  $Text" -ForegroundColor Gray
}

function Write-Ok([string] $Text) {
    Write-Host "  [OK] $Text" -ForegroundColor Green
}

function Write-Warn([string] $Text) {
    Write-Host "  [AVISO] $Text" -ForegroundColor DarkYellow
}

function Write-Err([string] $Text) {
    Write-Host "  [ERROR] $Text" -ForegroundColor Red
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
        [string[]] $Args
    )

    $prevErrorAction = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $rawOutput = & git @Args 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevErrorAction
    }

    $output = @($rawOutput | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            $_.ToString()
        }
        else {
            "$_"
        }
    })

    if ($exitCode -ne 0) {
        if ($output.Count -gt 0) {
            $output | ForEach-Object { Write-Err $_ }
        }
        throw "git $($Args -join ' ') falló con código $exitCode"
    }

    return $output
}

function Confirm-Continue {
    param(
        [string] $Prompt = "¿Continuar?",
        [bool] $DefaultYes = $true
    )

    if ($NonInteractive) {
        return $true
    }

    $suffix = if ($DefaultYes) { "[S/n]" } else { "[s/N]" }
    $answer = Read-Host "$Prompt $suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) {
        return $DefaultYes
    }

    return $answer -match '^(s|si|y|yes)$'
}

function Get-RepoRoot {
    $root = Invoke-Git "rev-parse" "--show-toplevel"
    return ($root | Select-Object -First 1).ToString().Trim()
}

function Test-GitWorkingTreeClean {
    $status = Invoke-Git "status" "--porcelain"
    return [string]::IsNullOrWhiteSpace(($status | Out-String).Trim())
}

function Ensure-UpstreamRemote {
    param([string] $RepoRoot)

    Push-Location $RepoRoot
    try {
        $remotes = Invoke-Git "remote"
        $remoteList = @($remotes | ForEach-Object { $_.ToString().Trim() })

        if ($remoteList -contains $UpstreamRemoteName) {
            $url = (Invoke-Git "remote" "get-url" $UpstreamRemoteName | Select-Object -First 1).ToString().Trim()
            Write-Ok "Remoto '$UpstreamRemoteName' ya configurado: $url"

            if ($url -ne $UpstreamUrl) {
                Write-Warn "La URL de upstream difiere de la esperada ($UpstreamUrl)"
                if (Confirm-Continue -Prompt "¿Actualizar la URL de upstream?" -DefaultYes $false) {
                    Invoke-Git "remote" "set-url" $UpstreamRemoteName $UpstreamUrl | Out-Null
                    Write-Ok "URL de upstream actualizada"
                }
            }
        }
        else {
            Write-Warn "No existe el remoto '$UpstreamRemoteName'"
            if (-not (Confirm-Continue -Prompt "¿Agregar upstream apuntando a $UpstreamUrl?" -DefaultYes $true)) {
                throw "Se canceló: upstream es necesario para continuar."
            }
            Invoke-Git "remote" "add" $UpstreamRemoteName $UpstreamUrl | Out-Null
            Write-Ok "Remoto '$UpstreamRemoteName' agregado"
        }
    }
    finally {
        Pop-Location
    }
}

function Show-CommitSummary {
    param(
        [string] $RepoRoot,
        [string] $LocalRef,
        [string] $UpstreamRef
    )

    Push-Location $RepoRoot
    try {
        $counts = (Invoke-Git "rev-list" "--left-right" "--count" "$LocalRef...$UpstreamRef" | Select-Object -First 1).ToString().Trim()
        $parts = $counts -split "\s+"
        $localOnly = [int]$parts[0]
        $upstreamOnly = [int]$parts[1]

        Write-Info "Commits solo en tu rama local:     $localOnly"
        Write-Info "Commits nuevos en upstream:         $upstreamOnly"

        if ($upstreamOnly -gt 0) {
            Write-Host ""
            Write-Host "  Últimos commits que traerás de upstream:" -ForegroundColor Gray
            $incoming = Invoke-Git "log" "--oneline" "--max-count=15" "$LocalRef..$UpstreamRef"
            $incoming | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            if ($upstreamOnly -gt 15) {
                Write-Info "(y $($upstreamOnly - 15) commits más...)"
            }
        }

        if ($localOnly -gt 0) {
            Write-Host ""
            Write-Host "  Tus commits locales que se conservarán:" -ForegroundColor Gray
            $local = Invoke-Git "log" "--oneline" "--max-count=10" "$UpstreamRef..$LocalRef"
            $local | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            if ($localOnly -gt 10) {
                Write-Info "(y $($localOnly - 10) commits más...)"
            }
        }

        return @{
            LocalOnly     = $localOnly
            UpstreamOnly  = $upstreamOnly
        }
    }
    finally {
        Pop-Location
    }
}

function Resolve-MergeConflictsGuide {
    Write-Title "Conflictos de merge detectados"
    Write-Host @"
  Git encontró archivos en conflicto. Sigue estos pasos:

    1. Revisa los archivos marcados:
         git status

    2. Abre cada archivo y busca las marcas:
         <<<<<<< HEAD
         (tu código local)
         =======
         (código de upstream)
         >>>>>>> upstream/develop

    3. Edita cada conflicto conservando lo que necesites de ambas versiones.

    4. Marca cada archivo como resuelto:
         git add <archivo>

    5. Finaliza el merge:
         git merge --continue

  Para cancelar y volver al estado anterior:
         git merge --abort

"@ -ForegroundColor Gray
}

function Resolve-RebaseConflictsGuide {
    Write-Title "Conflictos de rebase detectados"
    Write-Host @"
  Git pausó el rebase por conflictos. Sigue estos pasos:

    1. Revisa los archivos en conflicto:
         git status

    2. Resuelve cada conflicto manualmente en el editor.

    3. Marca como resuelto:
         git add <archivo>

    4. Continúa el rebase:
         git rebase --continue

  Para cancelar el rebase:
         git rebase --abort

"@ -ForegroundColor Gray
}

function Offer-Push {
    param([string] $BranchName)

    if ($SkipPush -or $NonInteractive) {
        return
    }

    Write-Step 8 "Publicar cambios (opcional)"
    Write-Info "Rama actual: $BranchName"

    $remotes = @(Invoke-Git "remote" | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -ne $UpstreamRemoteName })
    if ($remotes.Count -eq 0) {
        Write-Warn "No hay remotos configurados además de upstream."
        return
    }

    Write-Host ""
    Write-Host "  Remotos disponibles para publicar:" -ForegroundColor Gray
    for ($i = 0; $i -lt $remotes.Count; $i++) {
        $url = (Invoke-Git "remote" "get-url" $remotes[$i] | Select-Object -First 1).ToString().Trim()
        Write-Host "    [$($i + 1)] $($remotes[$i]) -> $url" -ForegroundColor DarkGray
    }

    if (-not (Confirm-Continue -Prompt "¿Publicar la rama '$BranchName' en algún remoto?" -DefaultYes $false)) {
        Write-Info "Publicación omitida. Puedes hacerlo más tarde con: git push <remoto> $BranchName"
        return
    }

    $choice = Read-Host "Número del remoto (1-$($remotes.Count))"
    $index = [int]$choice - 1
    if ($index -lt 0 -or $index -ge $remotes.Count) {
        Write-Warn "Selección inválida. Publicación omitida."
        return
    }

    $targetRemote = $remotes[$index]
    Write-Info "Ejecutando: git push $targetRemote $BranchName"
  if (Confirm-Continue -Prompt "¿Confirmar push a '$targetRemote'?" -DefaultYes $true) {
        try {
            Invoke-Git "push" $targetRemote $BranchName | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
            Write-Ok "Cambios publicados en $targetRemote/$BranchName"
        }
        catch {
            Write-Err "El push falló. Puede que necesites: git push -u $targetRemote $BranchName"
            throw
        }
    }
}

# --- Inicio del script ---

Write-Title "Sincronización con velopack/velopack"
Write-Host "  Este script te guiará para traer cambios del repositorio original" -ForegroundColor Gray
Write-Host "  y combinarlos con tus modificaciones locales de forma segura." -ForegroundColor Gray
Write-Host ""
Write-Host "  Repositorio upstream: $UpstreamUrl" -ForegroundColor Gray
Write-Host "  Rama upstream:       $UpstreamBranch" -ForegroundColor Gray

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Err "Git no está instalado o no está en el PATH."
    exit 1
}

Write-Step 1 "Verificar repositorio Git"
try {
    $repoRoot = Get-RepoRoot
    Write-Ok "Repositorio detectado: $repoRoot"
}
catch {
    Write-Err "Debes ejecutar este script desde la raíz de un repositorio Git."
    Write-Info "Uso: .\scripts\sync-upstream.ps1"
    exit 1
}

Push-Location $repoRoot
$stashCreated = $false

try {
    Write-Step 2 "Configurar remoto upstream"
    Ensure-UpstreamRemote -RepoRoot $repoRoot

    Write-Step 3 "Estado actual del repositorio"
    $currentBranch = (Invoke-Git "branch" "--show-current" | Select-Object -First 1).ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($currentBranch)) {
        throw "No estás en una rama con nombre (detached HEAD). Cambia a 'develop' u otra rama antes de continuar."
    }
    Write-Ok "Rama actual: $currentBranch"

    $tracking = Invoke-Git "status" "-sb" | Select-Object -First 1
    Write-Info $tracking

    $isClean = Test-GitWorkingTreeClean
    if ($isClean) {
        Write-Ok "El árbol de trabajo está limpio (sin cambios sin commitear)"
    }
    else {
        Write-Warn "Hay cambios locales sin commitear."
        Invoke-Git "status" "--short" | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }

        if ($NonInteractive) {
            throw "Hay cambios sin commitear. Haz commit o stash antes de ejecutar en modo no interactivo."
        }

        Write-Host ""
        Write-Host "  Opciones:" -ForegroundColor Gray
        Write-Host "    [1] Guardar temporalmente con stash (recomendado)" -ForegroundColor Gray
        Write-Host "    [2] Cancelar y commitear manualmente primero" -ForegroundColor Gray
        $dirtyChoice = Read-Host "Elige una opción [1/2]"

        if ($dirtyChoice -eq "1") {
            Invoke-Git "stash" "push" "-u" "-m" $StashMessage | Out-Null
            $stashCreated = $true
            Write-Ok "Cambios guardados en stash: '$StashMessage'"
            Write-Info "Recupéralos después con: git stash pop"
        }
        else {
            throw "Sincronización cancelada. Haz commit o stash de tus cambios y vuelve a ejecutar el script."
        }
    }

    if (-not (Confirm-Continue -Prompt "¿Continuar con la sincronización?" -DefaultYes $true)) {
        throw "Sincronización cancelada por el usuario."
    }

    Write-Step 4 "Descargar cambios de upstream"
    Write-Info "Ejecutando: git fetch $UpstreamRemoteName $UpstreamBranch"
    Invoke-Git "fetch" $UpstreamRemoteName $UpstreamBranch | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    Write-Ok "Fetch completado"

    $upstreamRef = "$UpstreamRemoteName/$UpstreamBranch"

    Write-Step 5 "Resumen de diferencias"
    $summary = Show-CommitSummary -RepoRoot $repoRoot -LocalRef $currentBranch -UpstreamRef $upstreamRef

    if ($summary.UpstreamOnly -eq 0) {
        Write-Ok "Tu rama ya está al día con $upstreamRef"
    }
    else {
        Write-Step 6 "Elegir estrategia de combinación"
        Write-Host ""
        Write-Host "  Merge (recomendado para forks con muchos commits locales):" -ForegroundColor Gray
        Write-Host "    - Crea un commit de merge explícito" -ForegroundColor DarkGray
        Write-Host "    - Preserva el historial tal como ocurrió" -ForegroundColor DarkGray
        Write-Host "    - Más seguro si ya compartiste tu rama con otros" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "  Rebase:" -ForegroundColor Gray
        Write-Host "    - Reaplica tus commits locales encima de upstream" -ForegroundColor DarkGray
        Write-Host "    - Historial más lineal, pero reescribe commits locales" -ForegroundColor DarkGray
        Write-Host "    - Requiere force push si ya publicaste la rama" -ForegroundColor DarkGray
        Write-Host ""

        $selectedStrategy = $Strategy
        if ($selectedStrategy -eq "Ask") {
            if ($NonInteractive) {
                $selectedStrategy = "Merge"
            }
            else {
                $strategyChoice = Read-Host "Estrategia [M]erge / [R]ebase (por defecto: Merge)"
                if ($strategyChoice -match '^(r|rebase)$') {
                    $selectedStrategy = "Rebase"
                }
                else {
                    $selectedStrategy = "Merge"
                }
            }
        }

        Write-Info "Estrategia seleccionada: $selectedStrategy"

        if ($selectedStrategy -eq "Rebase" -and -not $NonInteractive) {
            if (-not (Confirm-Continue -Prompt "Rebase reescribe historial. ¿Estás seguro?" -DefaultYes $false)) {
                $selectedStrategy = "Merge"
                Write-Info "Cambiado a Merge por seguridad."
            }
        }

        Write-Step 7 "Combinar cambios"
        try {
            if ($selectedStrategy -eq "Merge") {
                Write-Info "Ejecutando: git merge $upstreamRef"
                $mergeMsg = "Merge $upstreamRef into $currentBranch"
                Invoke-Git "merge" $upstreamRef "-m" $mergeMsg | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
                Write-Ok "Merge completado sin conflictos"
            }
            else {
                Write-Info "Ejecutando: git rebase $upstreamRef"
                Invoke-Git "rebase" $upstreamRef | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
                Write-Ok "Rebase completado sin conflictos"
            }
        }
        catch {
            $gitStatus = (& git status 2>&1 | Out-String)
            if ($gitStatus -match "rebase in progress") {
                Resolve-RebaseConflictsGuide
            }
            else {
                Resolve-MergeConflictsGuide
            }
            throw "La combinación requiere resolución manual de conflictos. Sigue la guía anterior y vuelve a ejecutar el script si necesitas publicar."
        }
    }

    if ($stashCreated) {
        Write-Host ""
        if (Confirm-Continue -Prompt "¿Restaurar los cambios guardados en stash?" -DefaultYes $true) {
            try {
                Invoke-Git "stash" "pop" | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
                Write-Ok "Stash restaurado"
            }
            catch {
                Write-Warn "Hubo conflictos al restaurar el stash. Resuélvelos manualmente con: git stash list / git stash show -p"
            }
        }
        else {
            Write-Info "Stash conservado. Recupéralo cuando quieras: git stash pop"
        }
    }

    Offer-Push -BranchName $currentBranch

    Write-Title "Sincronización finalizada"
    Write-Ok "Tu rama '$currentBranch' incluye los cambios de upstream."
    Write-Host ""
    Write-Host "  Comandos útiles:" -ForegroundColor Gray
    Write-Host "    git log --oneline --graph -20          # Ver historial reciente" -ForegroundColor DarkGray
    Write-Host "    dotnet build                           # Compilar solución .NET" -ForegroundColor DarkGray
    Write-Host "    dotnet test test/Velopack.Tests        # Ejecutar tests básicos" -ForegroundColor DarkGray
    Write-Host ""
}
catch {
    if ($stashCreated -and (Test-GitWorkingTreeClean)) {
        Write-Warn "Se creó un stash antes de la operación. Recupéralo con: git stash pop"
    }
    Write-Err $_.Exception.Message
    exit 1
}
finally {
    Pop-Location
}
