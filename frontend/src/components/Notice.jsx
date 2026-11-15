import React from "react";
import { MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, Button, makeStyles, tokens } from "@fluentui/react-components";
import { DismissRegular } from "@fluentui/react-icons";

const useStyles = makeStyles({
  body: { overflowWrap: "anywhere" },
  list: { margin: `${tokens.spacingVerticalXS} 0 0`, paddingLeft: tokens.spacingHorizontalL },
});

/**
 * Faixa de mensagem do Fluent 2. intent: success | warning | error | info.
 * Erros são anunciados imediatamente por leitores de tela; os demais, de forma educada.
 */
export default function Notice({ intent = "info", title, children, issues, actions, onDismiss, className }) {
  const styles = useStyles();
  return <div role={intent === "error" ? "alert" : "status"} data-intent={intent} className={className}>
    <MessageBar intent={intent} layout="auto">
      <MessageBarBody className={styles.body}>
        {title && <MessageBarTitle>{title}</MessageBarTitle>}
        {title && children ? <br/> : null}
        {children}
        {issues?.length > 0 && <ul className={styles.list}>{issues.map(issue => <li key={issue}>{issue}</li>)}</ul>}
      </MessageBarBody>
      {(actions || onDismiss) && <MessageBarActions
        containerAction={onDismiss && <Button appearance="transparent" aria-label="Fechar mensagem" icon={<DismissRegular/>} onClick={onDismiss}/>}>
        {actions}
      </MessageBarActions>}
    </MessageBar>
  </div>;
}
