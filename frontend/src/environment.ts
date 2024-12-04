import packageJson from '../package.json';

export const version = packageJson.version;
export const isLocalDev = import.meta.env.DEV;
