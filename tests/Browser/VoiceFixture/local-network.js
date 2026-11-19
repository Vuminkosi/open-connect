export const isFixtureUrl = value => {
  try {
    const url = new URL(value);
    return ['http:', 'ws:'].includes(url.protocol) && url.hostname === '127.0.0.1' &&
      ['5187', '5188'].includes(url.port) && !url.username && !url.password;
  } catch { return false; }
};
