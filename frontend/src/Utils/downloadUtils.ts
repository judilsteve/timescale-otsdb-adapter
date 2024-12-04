// Adapted from https://stackoverflow.com/a/9834261
export default function download(blob: Blob, filename: string) {
    const url = URL.createObjectURL(blob);

    const a = document.createElement('a');
    a.style.display = 'none';
    a.href = url;
    a.download = filename;

    document.body.appendChild(a);

    a.click();

    window.URL.revokeObjectURL(url);
}
