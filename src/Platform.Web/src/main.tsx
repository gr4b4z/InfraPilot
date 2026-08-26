import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { AuthProvider } from '@/components/auth/AuthProvider'
import { getPageTitle, loadRuntimeConfig } from '@/lib/runtimeConfig'
import { loadAuthConfig } from '@/lib/authConfig'

async function bootstrap() {
  // MSAL silent token renewal loads this app inside a sandboxed hidden iframe.
  // Skip bootstrapping there — the auth response is handled by MSAL via the URL hash.
  if (window.parent !== window && /[?#].*(code=|error=)/.test(window.location.href)) {
    return
  }

  await loadRuntimeConfig()
  await loadAuthConfig()
  document.title = getPageTitle()

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <AuthProvider>
        <App />
      </AuthProvider>
    </StrictMode>,
  )
}

void bootstrap()
