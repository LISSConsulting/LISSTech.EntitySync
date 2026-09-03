import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource-variable/archivo'
import '@fontsource-variable/nunito-sans'
import './index.css'
import App from './App'

const root = document.getElementById('root')
if (!root) throw new Error('Dashboard root element was not found.')

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
