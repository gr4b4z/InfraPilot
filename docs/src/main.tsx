import React from 'react';
import { hydrateRoot } from 'react-dom/client';
import App, { readRouteFromPath } from './App';
import './styles.css';

const initialRoute = readRouteFromPath(window.location.pathname);

hydrateRoot(
  document.getElementById('root')!,
  <React.StrictMode>
    <App initialRoute={initialRoute} />
  </React.StrictMode>,
);
