# Platform.Web

## Local development

```bash
npm run dev --prefix src/Platform.Web
```

The dev server proxies `/api` and `/agent` to the API (default `http://localhost:5259`), so the browser
only ever talks to the dev server's own origin. **There is no cross-origin request in local
development, and therefore no CORS to configure.** This mirrors production, where the container's nginx
proxies the same two prefixes.

Consequences worth knowing:

- **The dev-server port doesn't matter.** If 5173 is taken, Vite picks another and everything still
  works. It used to matter: `public/config.json` pointed the browser directly at `http://localhost:5259`,
  which made every call cross-origin and left the app dependent on the API's `Cors:AllowedOrigins`
  naming the exact port. On any other port the app loaded and showed nothing.
- **Leave `backendBaseUrl` empty in `public/config.json`.** Empty means "same origin", which is what
  makes the proxy work here and what nginx relies on in the container. Real deployments set it at
  container start via `BACKEND_BASE_URL` (see `infra/start-single-container.sh`); it is only worth
  setting for a deployment that serves the SPA and the API from different origins.
- **API somewhere else?** `VITE_API_TARGET=http://localhost:5300 npm run dev`.

The API additionally accepts any loopback origin when running in Development, so a scratch page or a
second dev server that bypasses the proxy isn't blocked either. Deployed environments still answer only
to the configured `Cors:AllowedOrigins`.

## React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR and some ESLint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the ESLint configuration

If you are developing a production application, we recommend updating the configuration to enable type-aware lint rules:

```js
export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...

      // Remove tseslint.configs.recommended and replace with this
      tseslint.configs.recommendedTypeChecked,
      // Alternatively, use this for stricter rules
      tseslint.configs.strictTypeChecked,
      // Optionally, add this for stylistic rules
      tseslint.configs.stylisticTypeChecked,

      // Other configs...
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```

You can also install [eslint-plugin-react-x](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-x) and [eslint-plugin-react-dom](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-dom) for React-specific lint rules:

```js
// eslint.config.js
import reactX from 'eslint-plugin-react-x'
import reactDom from 'eslint-plugin-react-dom'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...
      // Enable lint rules for React
      reactX.configs['recommended-typescript'],
      // Enable lint rules for React DOM
      reactDom.configs.recommended,
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```
