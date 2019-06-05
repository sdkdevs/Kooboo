import context from "../context";
import { getKoobooComment, isSkipHover } from "./domAnalyze";
import { SelectedDomEventArgs } from "../events/SelectedDomEvent";

export default (document: Document) => {
  document.body.addEventListener("mouseover", e => {
    e.stopPropagation();

    if (isSkipHover(e)) return;

    let el = e.target as HTMLElement;

    if (
      context.lastSelectedDomEventArgs &&
      context.lastSelectedDomEventArgs.element == el
    )
      return;

    let { koobooComment, koobooId, closeEl } = getKoobooComment(el);
    var args = new SelectedDomEventArgs(el, closeEl, koobooId, koobooComment);
    args.lastSelectedDomEventArgs = context.lastSelectedDomEventArgs;
    context.domChangeEvent.emit(args);
    context.lastSelectedDomEventArgs = args;
  });
};
