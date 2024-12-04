export default function jsonFetch(path: string, options?: RequestInit | undefined) {
    const headers = options?.method && ['PUT', 'POST'].includes(options.method) ? { 'Content-Type': 'application/json' } : undefined;
    return fetch(path, {
        headers,
        credentials: 'include',
        ...options
    });
}

export async function swrFetcher<T>(endpoint: string) {
    const response = await jsonFetch(endpoint);
    if(!response.ok) throw new Error(`Endpoint '${endpoint}' responded with status code ${response.status} (${response.statusText})`);
    return await response.json() as T;
}
