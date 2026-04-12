import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { AuthProvider } from '@/components/auth/AuthProvider'
import { getPageTitle, loadRuntimeConfig } from '@/lib/runtimeConfig'

async function bootstrap() {
  await loadRuntimeConfig()
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
