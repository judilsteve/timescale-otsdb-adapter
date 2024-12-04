import { Message, TransitionGroup } from 'semantic-ui-react';
import 'semantic/elements/icon.less';
import 'semantic/collections/message.less';
import 'semantic/elements/header.less';
import 'semantic/modules/transition.less';

import './ToastContainer.less';

import { useSharedState } from 'src/Hooks/useSharedState';
import { toastState } from 'src/State/sharedState';
import { useCallback } from 'react';

export default function ToastContainer() {
    const [ toasts, setToasts ] = useSharedState(toastState);

    const dismiss = useCallback((id: number) => {
        setToasts(toasts.filter(t => t.id !== id));
    }, [toasts, setToasts]);

    const defaultIcons = {
        info: 'announcement',
        success: 'checkmark',
        error: 'remove',
        warning: 'warning circle'
    };

    return <div id="toastContainer">
        <TransitionGroup animation='fade left' duration={200}>
            {
                // Need to wrap in a div to avoid TransitionGroup garbling Message layout
                toasts.map(t => <div key={t.id} className="toast"><Message
                    header={t.title}
                    content={t.description}
                    error={t.type === 'error'}
                    info={t.type === 'info'}
                    success={t.type === 'success'}
                    warning={t.type === 'warning'}
                    icon={t.icon ?? defaultIcons[t.type]}
                    onDismiss={() => dismiss(t.id)}
                /></div>)
            }
        </TransitionGroup>
    </div>;
}
