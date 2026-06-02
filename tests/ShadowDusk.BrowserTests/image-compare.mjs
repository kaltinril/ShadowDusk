// JS mirror of tests/ShadowDusk.ImageTests/ImageComparer.cs — max per-channel
// delta over RGBA8 buffers, with a per-pixel "different if max channel delta >
// tolerance" rule. Same semantics so the Phase 17 §6.1 tolerance policy applies
// unchanged.
export function compareRgba(expected, actual, tolerance = 0) {
  if (expected.length !== actual.length) {
    throw new Error(`buffer lengths differ: expected=${expected.length} actual=${actual.length}`);
  }
  if ((expected.length & 3) !== 0) {
    throw new Error(`buffer length ${expected.length} not a multiple of 4 (RGBA8 required)`);
  }
  const totalPixels = expected.length / 4;
  let differentPixels = 0;
  let maxDelta = 0;
  for (let i = 0; i < expected.length; i += 4) {
    const dR = Math.abs(expected[i] - actual[i]);
    const dG = Math.abs(expected[i + 1] - actual[i + 1]);
    const dB = Math.abs(expected[i + 2] - actual[i + 2]);
    const dA = Math.abs(expected[i + 3] - actual[i + 3]);
    let pm = dR;
    if (dG > pm) pm = dG;
    if (dB > pm) pm = dB;
    if (dA > pm) pm = dA;
    if (pm > maxDelta) maxDelta = pm;
    if (pm > tolerance) differentPixels++;
  }
  return {
    matches: differentPixels === 0,
    differentPixels,
    totalPixels,
    maxChannelDelta: maxDelta,
  };
}
