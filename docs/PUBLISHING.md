# Publishing SkyRender to nuget.org

A step-by-step guide for a first-time publisher. It covers the one-time account setup, the first
release by hand (so you see every piece), and then the automated release workflow for every later
version.

Two packages ship together and always share one version number:

| Package | Depends on | Contents |
|---|---|---|
| `SkyRender` | — | NIF parsing, materials, textures, the OpenGL renderer, offscreen rendering |
| `SkyRender.Character` | `SkyRender` (same version range) | NPC assembly, the character viewer and its WPF panels |

Push `SkyRender` first: nuget.org validates each package's dependencies, and `SkyRender.Character`
cannot be installed until the `SkyRender` version it needs is listed.

> **Read this before the first push.** nuget.org never deletes a package version. You can *unlist*
> a version (it disappears from search), but anyone who asks for that exact version can still
> download it, forever. Everything inside a package (code, names, comments in shaders, the README,
> the PDB) is effectively permanent once pushed. Inspect the exact files you are about to push
> (2.3), every time.

---

## Part 1 — One-time setup

### 1.1 Create the nuget.org account

1. Go to <https://www.nuget.org> and choose **Sign in**. nuget.org signs in with a **Microsoft
   account**; create one if needed.
2. Pick a nuget.org **user name** (your public profile name, shown as the package owner). This is
   separate from the `Authors` text in the project file, which is free text.
3. Turn on two-factor authentication for the Microsoft account. The account can publish code that
   other people's builds download automatically; protect it like a bank login.

### 1.2 (Optional) Reserve the ID prefix

A reserved prefix means only your account can publish packages whose IDs start with it, and shows
a "verified" checkmark beside them.

- Email **account@nuget.org** with your nuget.org display name and the prefix (`SkyRender`).
- nuget.org judges whether the prefix clearly identifies you and is not a generic word; short or
  generic prefixes are refused. `SkyRender` may or may not pass; the request costs nothing.
- Do this **after** the first release, so there is a real package to point to.

### 1.3 Create a GitHub repository for the source

The packages point at their source (`RepositoryUrl`, and Source Link inside the symbol files), so
the repository must exist and be public before the first push. The extraction step of the split
creates it with a single reviewed commit; push that commit first.

---

## Part 2 — The first release, by hand

### 2.1 Set the version

Both project files share one version (in `Directory.Build.props` → `<Version>`). Semantic
versioning: **major** = breaking change, **minor** = new features, **patch** = fixes. The first
public release is `3.0.0` (it breaks the 2.x API the local projects used).

Also update `SkyRenderInfo.Version` (the runtime constant hosts compare against) to the same
value. The build fails if they disagree.

### 2.2 Build the packages

**Build from a neutrally named folder.** `SkyRender.Character` is post-processed by Fody, which
writes the absolute path of the build folder into the DLL (a known Fody limitation that
`PathMap` cannot fix). Clone the repository to a folder whose path says nothing about you or your
machine, for example `C:\SkyRender`, and build there. Releases built by the GitHub workflow
(Part 3) use the runner's path (`D:\a\SkyRender\SkyRender`) and need no care.

From the repository root (PowerShell or Git Bash):

```bash
dotnet pack SkyRender.sln -c Release -o artifacts -p:ContinuousIntegrationBuild=true
```

- `-o artifacts` puts the packages in one folder.
- `ContinuousIntegrationBuild=true` makes the build deterministic and strips your local folder
  paths from the symbol files, so they reference repository-relative paths only.

You should get four files:

```
artifacts/SkyRender.3.0.0.nupkg
artifacts/SkyRender.3.0.0.snupkg
artifacts/SkyRender.Character.3.0.0.nupkg
artifacts/SkyRender.Character.3.0.0.snupkg
```

The `.snupkg` files are symbol packages (debug information). nuget.org hosts them on its symbol
server so a developer can step into SkyRender's code while debugging their own app.

### 2.3 Inspect what you are about to publish

A `.nupkg` is a zip file. Look inside before pushing:

```bash
mkdir -p /tmp/inspect && cd /tmp/inspect && unzip -o ~/path/to/artifacts/SkyRender.3.0.0.nupkg
```

Check:

- `SkyRender.nuspec`: id, version, authors, description, license expression (`GPL-3.0-only`),
  project and repository URLs, dependencies with sensible version ranges.
- `README.md` renders sensibly (nuget.org shows it on the package page).
- `lib/net8.0/` (or `lib/net8.0-windows10.0.19041/` for Character) holds only the expected DLL.
- Nothing unexpected (test data, local paths, logs).

Fody writes the build-folder path of the PDB into `SkyRender.Character.dll` (2.2). Check that it
points into your neutral build folder and reveals nothing else:

```bash
grep -a -o -E '[A-Za-z]:[\/][^"<>|[:cntrl:]]*\.pdb' lib/net8.0-windows10.0.19041/SkyRender.Character.dll
```

### 2.4 Try the packages locally first

Point a scratch project at the `artifacts` folder and install from it, so you catch a broken
package before the world can:

```bash
dotnet new console -o /tmp/skyrender-smoke && cd /tmp/skyrender-smoke
dotnet nuget add source ~/path/to/artifacts --name skyrender-local
dotnet add package SkyRender --version 3.0.0 --source skyrender-local
dotnet build
```

Afterwards remove the source again: `dotnet nuget remove source skyrender-local`.

### 2.5 Create an API key (first release only)

1. nuget.org → your user name (top right) → **API Keys** → **Create**.
2. **Key name:** e.g. `skyrender-first-release`.
3. **Expires in:** the shortest option you are comfortable with (you only need it today).
4. **Select Scopes:** **Push**, and choose **Push new packages and package versions**.
5. **Select Packages → Glob Pattern:** `SkyRender*` (limits the key to these package IDs).
6. **Create**, then **Copy** the key. nuget.org shows it once; treat it like a password. Do not
   paste it into chat, a file in the repository, or a commit.

### 2.6 Push

Run from the `artifacts` folder. Paste the key only into your own terminal:

```bash
dotnet nuget push SkyRender.3.0.0.nupkg --api-key <YOUR-KEY> --source https://api.nuget.org/v3/index.json
dotnet nuget push SkyRender.Character.3.0.0.nupkg --api-key <YOUR-KEY> --source https://api.nuget.org/v3/index.json
```

With the matching `.snupkg` beside each `.nupkg`, the push uploads the symbol package after the
main one. (With .NET SDK 10.0.300 or newer you can instead set the `NUGET_API_KEY` environment
variable for that terminal session and omit `--api-key`.)

### 2.7 Wait for validation

Each package is virus-scanned and validated, then indexed. That usually takes under 15 minutes.
Until then the package shows as **Unlisted** under *Manage packages* with a "not yet published"
banner. You get an email when it is live (or one explaining a validation failure). If it takes
over an hour, check <https://status.nuget.org>.

### 2.8 Verify from a clean restore

```bash
dotnet new console -o /tmp/skyrender-verify && cd /tmp/skyrender-verify
dotnet add package SkyRender.Character --version 3.0.0
dotnet build
```

This pulls both packages from nuget.org itself.

### 2.9 Delete the first-release API key

nuget.org → **API Keys** → delete `skyrender-first-release`. Later releases use Trusted
Publishing (Part 3), which needs no stored key.

---

## Part 3 — Every later release: Trusted Publishing from GitHub Actions

Trusted Publishing lets a GitHub Actions workflow publish without any stored API key: GitHub
issues a short-lived signed token, nuget.org checks it against a policy you configure, and returns
an API key that lives for one hour.

### 3.1 Create the policy (once)

1. nuget.org → your user name → **Trusted Publishing** → add a policy. (nuget.org rolled this out
   gradually; if you don't see the menu, use a scoped API key stored as a GitHub secret instead.)
2. Fill in, for a repository `https://github.com/<you>/SkyRender` with the workflow
   `.github/workflows/release.yml`:
   - **Repository Owner:** `<you>`
   - **Repository:** `SkyRender`
   - **Workflow File:** `release.yml` (the file name only, no `.github/workflows/` prefix)
   - **Environment:** `release` (matches `environment: release` in the workflow; optional but
     recommended, because GitHub can then require your manual approval for every release)
3. A new policy can start "temporarily active" for 7 days; the first successful publish makes it
   permanent. If the 7 days pass, restart the window from the same page.

### 3.2 In GitHub (once)

- Repository → **Settings → Environments → New environment** → `release`. Under **Deployment
  protection rules**, add yourself as a **required reviewer**, so a release waits for your click.
- Repository → **Settings → Secrets and variables → Actions → New repository secret**:
  `NUGET_USER` = your nuget.org user name (profile name, not your email).

### 3.3 Release

1. Bump the version (2.1) and update `CHANGELOG.md`; commit.
2. Inspect a local `dotnet pack` of that commit (2.3).
3. Tag and push the tag:

   ```bash
   git tag v3.1.0
   git push origin v3.1.0
   ```

4. The `release.yml` workflow builds, tests, packs, and waits for your approval in the `release`
   environment; approve it in the **Actions** tab. It then logs in with `NuGet/login@v1` and
   pushes `SkyRender` and then `SkyRender.Character`.
5. Verify (2.7, 2.8).

---

## Part 4 — If something goes wrong after publishing

- **Bug in a release:** publish a patch version (`3.0.1`). Packages are immutable; you cannot
  replace `3.0.0`.
- **A release must not be used:** on nuget.org open the package → **Manage package** →
  **Listing**, select the version, untick **List in search results**, **Save**. Also consider
  **Deprecation** on the same page, which warns anyone already using it. Unlisted versions remain
  downloadable by exact version number.
- **Content that must not be public at all** (for example a leaked credential): unlisting is not
  enough. Rotate the credential immediately, then use **Contact support** on the package page;
  nuget.org deletes packages only in exceptional cases (such as copyright problems or harmful
  content).

---

## Sources

- [How to publish NuGet packages](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package)
- [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
- [ID Prefix Reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation)
- [Deleting packages from nuget.org](https://learn.microsoft.com/en-us/nuget/nuget-org/policies/deleting-packages)
- [Symbol packages (.snupkg)](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg)
