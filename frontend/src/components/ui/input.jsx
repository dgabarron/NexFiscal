import React from "react";
import { cn } from "../../lib/utils";
export const Input=React.forwardRef(function Input({className,...props},ref){return <input ref={ref} className={cn("flex h-11 w-full rounded-lg border border-slate-200 bg-white px-3.5 text-sm text-slate-800 outline-none transition-shadow placeholder:text-slate-400 focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:bg-slate-50 disabled:opacity-60",className)} {...props}/>;});
