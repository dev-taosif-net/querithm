// Vitest setup: QUERITHM_TEST_TIME_SCALE (ci.yml sets 4 on the slow macos-15-intel runner, 1 elsewhere) stretches
// Testing Library's async waits (findBy* / waitFor, 1 s by default) as it stretches the sidecar tests' limits (TestTime).
// A 300 ms debounce plus a render missed the 1 s wait there (RenameDialog, CI run 43577279333). Scale 1 changes nothing,
// and the module is loaded only when scaling, so node-environment test files do not pay for it.
export const timeScale = Math.max(1, Number(process.env.QUERITHM_TEST_TIME_SCALE) || 1);

if (timeScale > 1) {
  const { configure } = await import("@testing-library/react");
  configure({ asyncUtilTimeout: 1000 * timeScale });
}
