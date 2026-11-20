// Translate the visible approval wording for the existing scenario policies.
// Unknown wording is denied by those policies rather than guessed.
export async function approvalDetails(dialog) {
  const content = dialog.locator('.mud-dialog-content');
  if (await content.count() === 0)
    return await dialog.locator('p').innerText();

  const paragraphs = content.locator('p');
  const description = await paragraphs.first().innerText();
  const patterns = [
    [/^Switch to the (.+) view\?$/, 'SelectTab'],
    [/^Change the selection for (.+)\?$/, 'Select'],
    [/^Change (.+)\?$/, 'SetValue'],
    [/^Activate (.+)\?$/, 'Click'],
    [/^Focus on (.+)\?$/, 'Focus'],
    [/^Scroll to (.+)\?$/, 'Scroll'],
    [/^Highlight (.+)\?$/, 'Highlight'],
    [/^Clear the highlight on (.+)\?$/, 'ClearHighlight'],
    [/^Inspect (.+)\?$/, 'Inspect'],
    [/^Wait for (.+) to change\?$/, 'WaitForState'],
    [/^Close (.+)\?$/, 'Close'],
  ];
  const match = patterns.map(([pattern, action]) => ({ match: description.match(pattern), action }))
    .find((entry) => entry.match);
  if (!match) return '';
  let result = `Allow ${match.action} on '${match.match[1]}'?`;
  const valueLabel = content.getByText('Requested value', { exact: true });
  if (await valueLabel.count() === 1) {
    const value = await content.locator('.mud-paper').innerText();
    result += `\nValue: ${value}`;
  }
  return result;
}
