declare const __APP_VERSION__: string;

interface Window {
  /**
   * The boot watchdog's handle, installed by the inline script in index.html before the bundle runs.
   * Absent only if that script didn't run, so every caller treats it as optional.
   */
  __ipBoot?: {
    /** The app has rendered — stand down, and take down the "couldn't load" panel if it is up. */
    done(): void;
    /**
     * Reloads past any stale cached copy of the page. With `reset`, first clears this origin's
     * local/session storage, `ip.*` preference cookies, Cache Storage and service workers.
     */
    reload(reset: boolean): void;
  };
}
