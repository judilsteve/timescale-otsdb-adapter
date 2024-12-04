import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react-swc';
import tsconfigPaths from 'vite-tsconfig-paths';
import { viteStaticCopy } from 'vite-plugin-static-copy';

// https://vitejs.dev/config/
export default function(mode: string){
    const viteEnv = {...process.env, ...loadEnv(mode, process.cwd())};

    return defineConfig({
        plugins: [
            react(),
            tsconfigPaths(),
            viteStaticCopy({
                targets: [
                    {
                        src: 'favicon/*',
                        dest: ''
                    },
                ]
            })
        ],
        server: {
            watch: {
                // Required to make hot-reload work properly when running on WSL2 with code cloned to Windows partition
                usePolling: true,
            },
            fs: {
                // See https://github.com/withastro/astro/issues/6022
                strict: false,
            },
            proxy: {
                '/api': {
                    target: viteEnv.VITE_DEV_PROXY_TARGET,
                }
            }
        },
        build: {
            sourcemap: true
        }
    })
}
