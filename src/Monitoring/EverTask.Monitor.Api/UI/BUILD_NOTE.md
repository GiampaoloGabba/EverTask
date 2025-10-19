# UI Build Instructions

## ✅ FIXED - Build Now Works on Windows!

The Vite build issue on Windows has been resolved by moving `@rollup/rollup-win32-x64-msvc` from `optionalDependencies` to regular `dependencies` in `package.json`.

### Building on Windows

```bash
cd E:\Archivio\Sviluppo\Web\EverTask\src\Monitoring\EverTask.Monitor.Api\UI
npm install
npm run build
```

This now works correctly on Windows without any workarounds!

### Previous Issue (RESOLVED)

~~There was a known npm bug on Windows with optional dependencies that prevented Rollup from installing correctly: https://github.com/npm/cli/issues/4828~~

**Solution Applied**: The Windows-specific Rollup binary (`@rollup/rollup-win32-x64-msvc`) is now a required dependency instead of optional, ensuring it's always installed on Windows.

### Alternative Package Managers (Optional)

You can also use alternative package managers if preferred:

```bash
# Using Yarn
yarn install
yarn build

# Using pnpm
pnpm install
pnpm build
```

## Build Output

After successful build:
- Output directory: `../wwwroot/`
- The `wwwroot/` folder is embedded into the NuGet package as embedded resources
- The `UI/` source folder is NOT included in the NuGet package (dev-only)

## Development

For development with hot reload:
```bash
npm run dev
# Access at: http://localhost:5173
# API proxied to: http://localhost:5000
```
