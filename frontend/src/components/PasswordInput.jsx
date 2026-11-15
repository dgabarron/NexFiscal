import React, { useState } from "react";
import { Button, Input } from "@fluentui/react-components";
import { EyeOffRegular, EyeRegular } from "@fluentui/react-icons";

/** Campo de senha com botão mostrar/ocultar. Nunca usa preenchimento automático do navegador. */
export default function PasswordInput({ value, onChange, placeholder, ...rest }) {
  const [visible, setVisible] = useState(false);
  return <Input {...rest} value={value} placeholder={placeholder} type={visible ? "text" : "password"} autoComplete="new-password"
    onChange={(_, data) => onChange(data.value)}
    contentAfter={<Button appearance="transparent" size="small" aria-label={visible ? "Ocultar senha" : "Mostrar senha"}
      icon={visible ? <EyeOffRegular/> : <EyeRegular/>} onClick={() => setVisible(!visible)}/>}/>;
}
