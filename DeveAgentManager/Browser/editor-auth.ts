(async () => {
  const values = new URLSearchParams(location.hash.slice(1));
  if (values.get('theme') === 'light') document.documentElement.dataset.theme = 'light';
  values.delete('theme');
  history.replaceState(null, '', location.pathname);
  const response = await fetch(location.pathname, { method: 'POST', body: values, credentials: 'same-origin' });
  if (response.redirected) location.replace(response.url);
  else {
    document.querySelector('.spinner').remove();
    document.getElementById('status').textContent = 'This link has expired. Open VS Code from the Manager again.';
  }
})();
