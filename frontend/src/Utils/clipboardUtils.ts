import { toast } from 'src/State/sharedState';

export default async function copyToClipboard<T>(obj: T) {
    try {
        await navigator.clipboard.writeText(JSON.stringify(obj, undefined, 4));
    } catch(e) {
        console.error('Could not copy JSON to clipboard', e);
        toast({
            type: 'error',
            title: 'Failed to copy JSON to clipboard',
            description: 'Check your browser\'s clipboard permisisons.'
        });
        return;
    }
    toast({
        type: 'success',
        title: 'Copied to clipboard',
        description: 'JSON copied to clipboard.'
    });
}
