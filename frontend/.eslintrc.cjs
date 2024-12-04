module.exports = {
    root: true,
    env: { browser: true, es2020: true },
    extends: [
        'eslint:recommended',
        'plugin:@typescript-eslint/recommended-type-checked',
        'plugin:react-hooks/recommended',
        'plugin:react/recommended',
        'plugin:react/jsx-runtime',
    ],
    ignorePatterns: ['dist', '.eslintrc.cjs', 'fomantic-ui-less'],
    parser: '@typescript-eslint/parser',
    plugins: ['react-refresh'],
    rules: {
        'react-refresh/only-export-components': [
            'warn',
            { allowConstantExport: true },
        ],
        'no-console': [
            'error',
            {
                'allow': [
                    'warn',
                    'error'
                ]
            }
        ],
        'no-debugger': [ 'error' ],
        '@typescript-eslint/no-unused-vars': [
            'error',
            {
                // Allows pruning props from objects via destructuring, e.g.
                // const { propToDiscard, ...theRest } = objectToPrune;
                "ignoreRestSiblings": true
            }
        ],
    },
    parserOptions: {
        ecmaVersion: 'latest',
        sourceType: 'module',
        project: ['./tsconfig.json', './tsconfig.node.json'],
        tsconfigRootDir: __dirname,
    },
    settings: {
        react: {
            version: 'detect'
        }
    },
}
