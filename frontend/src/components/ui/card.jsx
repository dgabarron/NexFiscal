import React from "react";
import { cn } from "../../lib/utils";
export function Card({className,...props}){return <div className={cn("rounded-2xl border border-slate-200 bg-white shadow-[0_8px_30px_rgba(15,23,42,0.035)]",className)} {...props}/>;}
export function CardHeader({className,...props}){return <div className={cn("px-7 pt-7 pb-5",className)} {...props}/>;}
export function CardContent({className,...props}){return <div className={cn("px-7 pb-7",className)} {...props}/>;}
